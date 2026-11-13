//! The pinned public hostfxr C ABI. This module loads nothing until every sandbox control is sealed.
use std::ffi::{CStr, CString};

use super::hostfxr_abi::{Close, GetProperty, Handle, Initialize, InitializeParameters, Run};
use super::{
    Launch, ManagedProfile, RUNTIME_VERSION, RuntimeOperation, SDK_VERSION, SandboxError,
    XML_COMPARISON_ARGUMENT,
};

pub(super) fn run(launch: &Launch) -> Result<i32, SandboxError> {
    let failure = || SandboxError::Execute;
    let root = launch.dotnet_root.to_str().map_err(|_| failure())?;
    let host = CString::new(format!("{root}/dotnet")).map_err(|_| failure())?;
    let library = CString::new(format!("{root}/host/fxr/{RUNTIME_VERSION}/libhostfxr.so"))
        .map_err(|_| failure())?;
    let arguments = match &launch.profile {
        ManagedProfile::Compile => {
            let directory = std::env::current_dir().map_err(|_| failure())?;
            vec![
                CString::new("exec").unwrap(),
                CString::new(format!("{root}/sdk/{SDK_VERSION}/Roslyn/bincore/csc.dll"))
                    .map_err(|_| failure())?,
                CString::new("-noconfig").unwrap(),
                CString::new(format!(
                    "@{}/compiler.rsp",
                    directory.to_str().ok_or_else(failure)?
                ))
                .map_err(|_| failure())?,
            ]
        }
        ManagedProfile::Runtime(assembly, RuntimeOperation::Mapping) => vec![assembly.clone()],
        ManagedProfile::Runtime(assembly, RuntimeOperation::XmlCanonicalComparison) => vec![
            assembly.clone(),
            CString::new(XML_COMPARISON_ARGUMENT).unwrap(),
        ],
    };
    let argv = arguments
        .iter()
        .map(|value| value.as_ptr())
        .collect::<Vec<_>>();
    let app = match &launch.profile {
        ManagedProfile::Compile => format!("{root}/sdk/{SDK_VERSION}/Roslyn/bincore/csc.dll"),
        ManagedProfile::Runtime(path, _) => path.to_str().map_err(|_| failure())?.to_owned(),
    };
    let app_root = app.rsplit_once('/').ok_or_else(failure)?.0;
    if app_root.is_empty() {
        return Err(failure());
    }
    let app_deps = format!(
        "{}.deps.json",
        app.strip_suffix(".dll").ok_or_else(failure)?
    );
    let runtime_root = format!("{root}/shared/Microsoft.NETCore.App/{RUNTIME_VERSION}");
    // No Drop/dlclose: CoreCLR and its callbacks remain resident until the process exits, even on an error.
    let module = unsafe { libc::dlopen(library.as_ptr(), libc::RTLD_NOW | libc::RTLD_LOCAL) };
    if module.is_null() {
        return Err(failure());
    }
    let symbol = |name: &CStr| {
        let pointer = unsafe { libc::dlsym(module, name.as_ptr()) };
        if pointer.is_null() {
            Err(failure())
        } else {
            Ok(pointer)
        }
    };
    // Exact signatures from the pinned hostfxr.h; POSIX dlsym returns addresses of these C functions.
    let initialize: Initialize =
        unsafe { std::mem::transmute(symbol(c"hostfxr_initialize_for_dotnet_command_line")?) };
    let run: Run = unsafe { std::mem::transmute(symbol(c"hostfxr_run_app")?) };
    let close: Close = unsafe { std::mem::transmute(symbol(c"hostfxr_close")?) };
    let get: GetProperty =
        unsafe { std::mem::transmute(symbol(c"hostfxr_get_runtime_property_value")?) };
    let parameters = InitializeParameters {
        size: size_of::<InitializeParameters>(),
        host_path: host.as_ptr(),
        dotnet_root: launch.dotnet_root.as_ptr(),
    };
    let mut context: Handle = std::ptr::null_mut();
    let initialized =
        unsafe { initialize(argv.len() as i32, argv.as_ptr(), &parameters, &mut context) };
    if initialized != 0 || context.is_null() {
        if !context.is_null() {
            unsafe { close(context) };
        }
        return Err(failure());
    }
    // Refuse an alternate framework selected by a runtimeconfig, additional installation or roll-forward.
    // This is checked before run_app loads CoreCLR, not after managed startup.
    let property = |name: &CStr| -> Option<String> {
        let mut value: *const libc::c_char = std::ptr::null();
        if unsafe { get(context, name.as_ptr(), &mut value) } != 0 || value.is_null() {
            return None;
        }
        unsafe { CStr::from_ptr(value) }
            .to_str()
            .ok()
            .map(str::to_owned)
    };
    let expected = format!(
        "{root}/shared/Microsoft.NETCore.App/{RUNTIME_VERSION}/Microsoft.NETCore.App.deps.json"
    );
    let within = |path: &str| {
        [app_root, runtime_root.as_str()].iter().any(|base| {
            path.strip_prefix(base)
                .is_some_and(|suffix| suffix.starts_with('/'))
        }) && !path
            .split('/')
            .any(|component| component == ".." || component == ".")
    };
    let admitted = property(c"FX_DEPS_FILE").as_deref() == Some(expected.as_str())
        && property(c"APP_CONTEXT_DEPS_FILES").is_some_and(|paths| {
            let values = paths
                .split(';')
                .filter(|value| !value.is_empty())
                .collect::<Vec<_>>();
            values.contains(&expected.as_str())
                && values
                    .iter()
                    .all(|value| *value == expected || *value == app_deps)
        })
        && property(c"TRUSTED_PLATFORM_ASSEMBLIES").is_some_and(|paths| {
            !paths.is_empty() && paths.split(':').filter(|path| !path.is_empty()).all(within)
        })
        && property(c"NATIVE_DLL_SEARCH_DIRECTORIES").is_some_and(|paths| {
            paths
                .split(':')
                .filter(|path| !path.is_empty())
                .all(|path| within(path) || path == app_root || path == runtime_root)
        })
        && property(c"PLATFORM_RESOURCE_ROOTS").is_some_and(|paths| {
            paths
                .split(':')
                .filter(|path| !path.is_empty())
                .all(|path| within(path) || path == app_root || path == runtime_root)
        })
        && property(c"PROBING_DIRECTORIES").is_some_and(|paths| paths.is_empty())
        && property(c"APP_PATHS").is_none_or(|paths| {
            paths
                .split(':')
                .filter(|path| !path.is_empty())
                .all(|path| within(path) || path == app_root)
        });
    let result = if admitted {
        Some(unsafe { run(context) })
    } else {
        None
    };
    let closed = unsafe { close(context) };
    match result {
        Some(code) if closed == 0 => Ok(code),
        _ => Err(failure()),
    }
}
