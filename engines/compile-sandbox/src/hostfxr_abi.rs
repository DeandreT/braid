//! Exact public C signatures from dotnet/runtime v10.0.12 src/native/corehost/hostfxr.h.
use std::ffi::{c_char, c_void};

#[repr(C)]
pub(super) struct InitializeParameters {
    pub size: usize,
    pub host_path: *const c_char,
    pub dotnet_root: *const c_char,
}

pub(super) type Handle = *mut c_void;
pub(super) type Initialize = unsafe extern "C" fn(
    i32,
    *const *const c_char,
    *const InitializeParameters,
    *mut Handle,
) -> i32;
pub(super) type Run = unsafe extern "C" fn(Handle) -> i32;
pub(super) type Close = unsafe extern "C" fn(Handle) -> i32;
pub(super) type GetProperty =
    unsafe extern "C" fn(Handle, *const c_char, *mut *const c_char) -> i32;

#[cfg(all(test, target_pointer_width = "64"))]
mod tests {
    use super::*;

    #[test]
    fn pinned_hostfxr_x64_abi_layout() {
        assert_eq!(size_of::<InitializeParameters>(), 24);
        assert_eq!(align_of::<InitializeParameters>(), 8);
        assert_eq!(std::mem::offset_of!(InitializeParameters, size), 0);
        assert_eq!(std::mem::offset_of!(InitializeParameters, host_path), 8);
        assert_eq!(std::mem::offset_of!(InitializeParameters, dotnet_root), 16);
        assert_eq!(size_of::<Handle>(), 8);
        assert_eq!(size_of::<Initialize>(), 8);
        assert_eq!(size_of::<Run>(), 8);
        assert_eq!(size_of::<Close>(), 8);
        assert_eq!(size_of::<GetProperty>(), 8);
    }
}
