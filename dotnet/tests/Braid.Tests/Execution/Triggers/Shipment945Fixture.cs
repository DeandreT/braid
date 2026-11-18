using System.Globalization;
using System.Text;
using System.Text.Json;
using Braid.Application.Regression;
using Braid.Deployment;

namespace Braid.Tests;

// An intentionally narrow test mapping. Not a partner guide or an executable mapping DLL.
internal sealed class Shipment945Fixture(string image) : ICompiledBuildRuntime
{
    internal static byte[] Input => """{"order":"PO458","shipment":"SHIP458","shipDate":"20261118","sku":"SKU42","quantity":7}"""u8.ToArray();
    private const string Header = "ISA*00*          *00*          *ZZ*BRAIDWAREHOUSE *ZZ*BRAIDCUSTOMER  *261118*1200*U*00401*000000458*0*T*:~GS*SW*BRAIDWAREHOUSE*BRAIDCUSTOMER*20261118*1200*458*X*004010~ST*945*0458~";
    private const string Trailer = "SE*6*0458~GE*1*458~IEA*1*000000458~";
    internal static byte[] Expected => Encoding.ASCII.GetBytes(Header + "W06*F*PO458*20261118*SHIP458~LX*1~W12*CC*7*7*0*EA**VN*SKU42~W03*7~" + Trailer);
    internal static byte[] NativeExpected => Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(Expected).Replace("~", "~\n", StringComparison.Ordinal));
    internal byte[]? Last { get; private set; }
    public string RuntimeImage => image;
    public Task<CompiledBuildResult> ExecuteAsync(BuildArtifactReference artifact, BuildBytes bytes, RegressionReplay replay, CancellationToken token)
    {
        Last = bytes.Copy();
        using var parsed = JsonDocument.Parse(replay.Primary.CopyBytes());
        var data = parsed.RootElement;
        string Field(string key)
        {
            var value = data.GetProperty(key).GetString();
            return value is { Length: > 0 and <= 32 } && value.All(char.IsAsciiLetterOrDigit) ? value : throw new InvalidDataException();
        }
        var qty = data.GetProperty("quantity").GetInt32();
        if (qty < 1) return Task.FromResult(new CompiledBuildResult(CompiledBuildOutcome.InvalidInput));
        var n = qty.ToString(CultureInfo.InvariantCulture);
        var output = Encoding.ASCII.GetBytes(Header + $"W06*F*{Field("order")}*{Field("shipDate")}*{Field("shipment")}~LX*1~W12*CC*{n}*{n}*0*EA**VN*{Field("sku")}~W03*{n}~" + Trailer);
        return Task.FromResult(new CompiledBuildResult(CompiledBuildOutcome.Completed, RegressionOutputSet.Capture([
            RegressionOutput.Capture(RegressionOutputKey.Primary, output, token, mediaType: "application/edi-x12")])));
    }
}
