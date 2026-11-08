using System.Buffers.Binary;
using System.Text;
using Weft.Deployment;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

public sealed class CompileWorkerIdentityTests
{
    [Fact]
    public void NativeAndToolchainIdentitiesBothBindTheCompilationIdentity()
    {
        var native = "sha256:" + new string('1', 64);
        var toolchain = "sha256:" + new string('2', 64);
        var first = CompileWorkerIdentity.Combine(native, toolchain);
        Assert.Equal(first, CompileWorkerIdentity.Combine(native, toolchain));
        Assert.NotEqual(first, CompileWorkerIdentity.Combine("sha256:" + new string('3', 64), toolchain));
        Assert.NotEqual(first, CompileWorkerIdentity.Combine(native, "sha256:" + new string('3', 64)));
        Assert.NotEqual(native, first);
        Assert.Throws<ArgumentException>(() => CompileWorkerIdentity.Combine(native, "latest"));
    }

    [Fact]
    public void HostRuntimeCompilerReferencesAndLauncherAllAffectTheToolchainIdentity()
    {
        var root = Directory.CreateTempSubdirectory("weft-toolchain-identity-").FullName;
        try
        {
            var compiler = Path.Combine(root, "compiler");
            var references = Path.Combine(root, "references");
            var paths = new[]
            {
                Path.Combine(root, "dotnet"),
                Path.Combine(root, "host/fxr/version/hostfxr"),
                Path.Combine(root, "shared/Microsoft.NETCore.App", BuildWorkerProtocol.RuntimeVersion, "runtime"),
                Path.Combine(compiler, "csc.dll"),
                Path.Combine(references, "System.Runtime.dll"),
                Path.Combine(root, "system/ld-linux-x86-64.so.2"),
                Path.Combine(root, "launcher")
            };
            foreach (var path in paths)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "original");
            }
            string Identity() => ProcessCompileRunner.ComputeIdentity(root, compiler, references, paths[^1], [paths[^2]], []);
            var original = Identity();
            foreach (var path in paths)
            {
                File.WriteAllText(path, "changed");
                Assert.NotEqual(original, Identity());
                File.WriteAllText(path, "original");
                Assert.Equal(original, Identity());
            }
            File.WriteAllText(Path.Combine(compiler, "additional.dll"), "original");
            Assert.NotEqual(original, Identity());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EveryGrantedSystemLibraryTheCompilerCanLoadAffectsTheToolchainIdentity()
    {
        var root = Directory.CreateTempSubdirectory("weft-toolchain-libraries-").FullName;
        try
        {
            var toolchain = Path.Combine(root, "dotnet");
            var compiler = Path.Combine(toolchain, "compiler");
            var references = Path.Combine(toolchain, "references");
            var runtime = Path.Combine(toolchain, "shared/Microsoft.NETCore.App", BuildWorkerProtocol.RuntimeVersion);
            var system = Path.Combine(root, "lib");
            var multiarch = Path.Combine(system, "x86_64-linux-gnu");
            var launcher = Path.Combine(root, "launcher");
            var interpreter = Path.Combine(system, "ld-linux-x86-64.so.2");
            void Write(string path, byte[] content)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, content);
            }

            Write(Path.Combine(compiler, "csc.dll"), "compiler"u8.ToArray());
            Write(Path.Combine(references, "System.Runtime.dll"), "reference"u8.ToArray());
            Write(Path.Combine(toolchain, "host/fxr/version/libhostfxr.so"), Elf(["libstdc++.so.6"]));
            Write(Path.Combine(toolchain, "dotnet"), Elf(["libdl.so.2"]));
            Write(Path.Combine(runtime, "libcoreclr.so"), Elf(["libc.so.6"]));
            // A runtime library beside its dependency, found through $ORIGIN; the runtime itself is measured directly.
            Write(Path.Combine(runtime, "libmscordbi.so"), Elf(["libmscordaccore.so"], runPath: ["$ORIGIN"]));
            Write(Path.Combine(runtime, "libmscordaccore.so"), Elf(["libgcc_s.so.1"]));
            // Tracing needs a library the system does not install.
            Write(Path.Combine(runtime, "libcoreclrtraceptprovider.so"), Elf(["liblttng-ust.so.0"]));
            Write(launcher, Elf(["libm.so.6"]));
            Write(interpreter, "interpreter"u8.ToArray());
            Write(Path.Combine(multiarch, "libstdc++.so.6"), Elf(["libm.so.6"]));
            Write(Path.Combine(multiarch, "libdl.so.2"), "dl"u8.ToArray());
            Write(Path.Combine(multiarch, "libc.so.6"), "c"u8.ToArray());
            Write(Path.Combine(system, "libm.so.6"), "m"u8.ToArray());
            Write(Path.Combine(system, "libgcc_s.so.1"), "gcc"u8.ToArray());
            // OpenSSL is opened by name, and its own dependencies follow.
            Write(Path.Combine(multiarch, "libssl.so.3"), Elf(["libcrypto.so.3"]));
            Write(Path.Combine(multiarch, "libcrypto.so.3"), Elf(["libz.so.1"]));
            Write(Path.Combine(system, "libz.so.1"), "z"u8.ToArray());
            Write(Path.Combine(multiarch, "libunrelated.so.1"), "unrelated"u8.ToArray());
            string Identity() => ProcessCompileRunner.ComputeIdentity(toolchain, compiler, references, launcher, [interpreter], [system]);

            var original = Identity();
            Assert.Equal(original, Identity());
            foreach (var library in new[] { "x86_64-linux-gnu/libstdc++.so.6", "x86_64-linux-gnu/libdl.so.2", "x86_64-linux-gnu/libc.so.6",
                         "libm.so.6", "libgcc_s.so.1", "x86_64-linux-gnu/libssl.so.3", "x86_64-linux-gnu/libcrypto.so.3", "libz.so.1",
                         "ld-linux-x86-64.so.2" })
            {
                var path = Path.Combine(system, library);
                var content = File.ReadAllBytes(path);
                File.WriteAllBytes(path, [.. content, 0]);
                Assert.NotEqual(original, Identity());
                File.WriteAllBytes(path, content);
                Assert.Equal(original, Identity());
            }

            // Libraries outside the closure do not count; a newly installed dependency or another copy the loader
            // may pick does.
            File.WriteAllText(Path.Combine(multiarch, "libunrelated.so.1"), "changed");
            Assert.Equal(original, Identity());
            foreach (var added in new[] { "liblttng-ust.so.0", "glibc-hwcaps/x86-64-v3/libc.so.6", "x86_64-linux-gnu/libz.so.1" })
            {
                var path = Path.Combine(system, added);
                Write(path, "added"u8.ToArray());
                Assert.NotEqual(original, Identity());
                File.Delete(path);
                Assert.Equal(original, Identity());
            }

            // A malformed object fails the measurement instead of hiding its dependencies.
            var truncated = Elf(["libc.so.6"]);
            Write(Path.Combine(multiarch, "libssl.so.3"), truncated[..80]);
            Assert.Throws<InvalidDataException>(Identity);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void LibraryRunPathsFollowTheLoaderRules()
    {
        var root = Directory.CreateTempSubdirectory("weft-library-run-paths-").FullName;
        try
        {
            var toolchain = Path.Combine(root, "toolchain");
            var system = Path.Combine(root, "lib");
            var inherited = Path.Combine(root, "inherited");
            var direct = Path.Combine(root, "direct");
            var ignored = Path.Combine(root, "ignored");
            void Write(string path, byte[] content)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, content);
            }

            // Only the root carries DT_RPATH; the loader still searches it for the grandchild, whose parent has no path.
            Write(Path.Combine(toolchain, "host"), Elf(["liba.so.1"], rPath: ["$ORIGIN/../inherited"]));
            Write(Path.Combine(inherited, "liba.so.1"), Elf(["libb.so.1"]));
            Write(Path.Combine(inherited, "libb.so.1"), Elf(["libc.so.1"]));
            Write(Path.Combine(inherited, "libc.so.1"), "grandchild"u8.ToArray());
            // DT_RUNPATH serves only the object's own dependencies, and its DT_RPATH is ignored.
            Write(Path.Combine(toolchain, "runtime.so"), Elf(["libd.so.1", "libignored.so.1"], rPath: [ignored], runPath: [direct]));
            Write(Path.Combine(direct, "libd.so.1"), Elf(["libe.so.1"]));
            Write(Path.Combine(direct, "libe.so.1"), "not inherited"u8.ToArray());
            Write(Path.Combine(ignored, "libignored.so.1"), "ignored"u8.ToArray());
            Write(Path.Combine(system, "libe.so.1"), "system"u8.ToArray());

            var closure = ElfLibraryClosure.Resolve([Path.Combine(toolchain, "host"), Path.Combine(toolchain, "runtime.so")], [], [system]);
            Assert.Equal(new[]
            {
                Path.Combine(direct, "libd.so.1"), Path.Combine(inherited, "liba.so.1"), Path.Combine(inherited, "libb.so.1"),
                Path.Combine(inherited, "libc.so.1"), Path.Combine(system, "libe.so.1")
            }.Order(StringComparer.Ordinal), closure);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void The_surviving_launcher_interpreters_own_dependencies_are_measured()
    {
        var root = Directory.CreateTempSubdirectory("weft-loader-identity-").FullName;
        try
        {
            var compiler = Path.Combine(root, "compiler");
            var references = Path.Combine(root, "references");
            var host = Path.Combine(root, "host");
            var runtime = Path.Combine(root, "shared/Microsoft.NETCore.App", BuildWorkerProtocol.RuntimeVersion);
            var system = Path.Combine(root, "system");
            foreach (var path in new[] { compiler, references, host, runtime, system }) Directory.CreateDirectory(path);
            var launcher = Path.Combine(root, "launcher");
            var interpreter = Path.Combine(system, "ld-linux-x86-64.so.2");
            var dependency = Path.Combine(system, "libloader-only.so");
            File.WriteAllBytes(Path.Combine(root, "dotnet"), Elf([]));
            File.WriteAllBytes(launcher, Elf([]));
            File.WriteAllBytes(interpreter, Elf(["libloader-only.so"]));
            File.WriteAllBytes(dependency, "original"u8.ToArray());
            string Identity() => ProcessCompileRunner.ComputeIdentity(root, compiler, references, launcher, [interpreter], [system]);
            var first = Identity();
            File.WriteAllBytes(dependency, "changed"u8.ToArray());
            Assert.NotEqual(first, Identity());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A minimal x86-64 ELF shared object: one load segment holding its dynamic section and string table.</summary>
    internal static byte[] Elf(string[] needed, string[]? rPath = null, string[]? runPath = null)
    {
        var strings = new List<byte> { 0 };
        int Intern(string value)
        {
            var offset = strings.Count;
            strings.AddRange(Encoding.UTF8.GetBytes(value));
            strings.Add(0);
            return offset;
        }

        var entries = needed.Select(name => (Tag: 1L, Value: (ulong)Intern(name)))
            .Concat((rPath ?? []).Select(path => (Tag: 15L, Value: (ulong)Intern(path))))
            .Concat((runPath ?? []).Select(path => (Tag: 29L, Value: (ulong)Intern(path)))).ToList();
        const int dynamicOffset = 64 + 2 * 56;
        var dynamicSize = (entries.Count + 3) * 16;
        var stringOffset = dynamicOffset + dynamicSize;
        entries.Add((5, (ulong)stringOffset));
        entries.Add((10, (ulong)strings.Count));
        entries.Add((0, 0));
        var image = new byte[stringOffset + strings.Count];
        "\u007fELF"u8.CopyTo(image);
        image[4] = 2;
        image[5] = 1;
        image[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x10), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x12), 62);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x20), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x34), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x36), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x38), 2);
        void Segment(int index, uint type, int offset, int size)
        {
            var header = image.AsSpan(64 + index * 56);
            BinaryPrimitives.WriteUInt32LittleEndian(header, type);
            BinaryPrimitives.WriteUInt64LittleEndian(header[8..], (ulong)offset);
            BinaryPrimitives.WriteUInt64LittleEndian(header[16..], (ulong)offset);
            BinaryPrimitives.WriteUInt64LittleEndian(header[32..], (ulong)size);
            BinaryPrimitives.WriteUInt64LittleEndian(header[40..], (ulong)size);
        }

        Segment(0, 1, 0, image.Length);
        Segment(1, 2, dynamicOffset, dynamicSize);
        for (var index = 0; index < entries.Count; index++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(dynamicOffset + index * 16), entries[index].Tag);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(dynamicOffset + index * 16 + 8), entries[index].Value);
        }

        strings.CopyTo(image, stringOffset);
        return image;
    }
}
