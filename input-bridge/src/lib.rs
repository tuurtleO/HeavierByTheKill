#![allow(non_snake_case)]

use std::ffi::{c_char, c_void};
use std::mem;
use std::ptr;

type Handle = *mut c_void;
type XInputGetState = unsafe extern "system" fn(u32, *mut XInputState) -> u32;

const DLL_PROCESS_ATTACH: u32 = 1;
const FILE_MAP_ALL_ACCESS: u32 = 0x000f001f;
const SYNCHRONIZE: u32 = 0x0010_0000;
const WAIT_TIMEOUT: u32 = 0x0000_0102;
const ERROR_SUCCESS: u32 = 0;
const MAGIC: u32 = 0x4842_4b49; // HBKI

#[repr(C)]
struct SharedInput {
    magic: u32,
    owner_pid: u32,
    game_pid: u32,
    sequence: u32,
    buttons: u16,
    stop: u16,
}

#[repr(C)]
struct XInputGamepad {
    buttons: u16,
    left_trigger: u8,
    right_trigger: u8,
    thumb_lx: i16,
    thumb_ly: i16,
    thumb_rx: i16,
    thumb_ry: i16,
}

#[repr(C)]
struct XInputState {
    packet_number: u32,
    gamepad: XInputGamepad,
}

#[link(name = "kernel32")]
extern "system" {
    fn CloseHandle(handle: Handle) -> i32;
    fn CreateThread(
        attributes: *mut c_void,
        stack_size: usize,
        start: unsafe extern "system" fn(*mut c_void) -> u32,
        parameter: *mut c_void,
        flags: u32,
        thread_id: *mut u32,
    ) -> Handle;
    fn DisableThreadLibraryCalls(module: Handle) -> i32;
    fn FreeLibraryAndExitThread(module: Handle, exit_code: u32) -> !;
    fn GetCurrentProcessId() -> u32;
    fn GetModuleHandleW(name: *const u16) -> Handle;
    fn GetProcAddress(module: Handle, name: *const c_char) -> *mut c_void;
    fn MapViewOfFile(mapping: Handle, access: u32, high: u32, low: u32, size: usize)
        -> *mut c_void;
    fn OpenFileMappingW(access: u32, inherit: i32, name: *const u16) -> Handle;
    fn OpenProcess(access: u32, inherit: i32, pid: u32) -> Handle;
    fn Sleep(milliseconds: u32);
    fn UnmapViewOfFile(base: *const c_void) -> i32;
    fn WaitForSingleObject(handle: Handle, milliseconds: u32) -> u32;
}

static mut MODULE: Handle = ptr::null_mut();

fn wide(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(Some(0)).collect()
}

unsafe extern "system" fn input_thread(_: *mut c_void) -> u32 {
    // Steam's legacy input shim is process-scoped. Resolving XInput from inside
    // DSR lets us observe the same virtual controller that the game sees.
    let xinput_name = wide("xinput1_3.dll");
    let xinput_module = GetModuleHandleW(xinput_name.as_ptr());
    let proc = if xinput_module.is_null() {
        ptr::null_mut()
    } else {
        GetProcAddress(xinput_module, b"XInputGetState\0".as_ptr().cast())
    };
    if proc.is_null() {
        FreeLibraryAndExitThread(MODULE, 2);
    }
    let get_state: XInputGetState = mem::transmute(proc);

    let game_pid = GetCurrentProcessId();
    let mapping_name = wide(&format!("Local\\HeavierByTheKill.Input.{game_pid}"));
    let mut mapping = ptr::null_mut();
    for _ in 0..500 {
        mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, 0, mapping_name.as_ptr());
        if !mapping.is_null() {
            break;
        }
        Sleep(4);
    }
    if mapping.is_null() {
        FreeLibraryAndExitThread(MODULE, 3);
    }
    let shared = MapViewOfFile(
        mapping,
        FILE_MAP_ALL_ACCESS,
        0,
        0,
        mem::size_of::<SharedInput>(),
    ) as *mut SharedInput;
    if shared.is_null() {
        CloseHandle(mapping);
        FreeLibraryAndExitThread(MODULE, 4);
    }

    let owner = OpenProcess(SYNCHRONIZE, 0, ptr::read_volatile(&(*shared).owner_pid));
    ptr::write_volatile(&mut (*shared).magic, MAGIC);
    ptr::write_volatile(&mut (*shared).game_pid, game_pid);
    while ptr::read_volatile(&(*shared).stop) == 0
        && (owner.is_null() || WaitForSingleObject(owner, 0) == WAIT_TIMEOUT)
    {
        let mut buttons = 0u16;
        for index in 0..4 {
            let mut state: XInputState = mem::zeroed();
            if get_state(index, &mut state) == ERROR_SUCCESS {
                buttons |= state.gamepad.buttons;
            }
        }
        ptr::write_volatile(&mut (*shared).buttons, buttons);
        let sequence = ptr::read_volatile(&(*shared).sequence).wrapping_add(1);
        ptr::write_volatile(&mut (*shared).sequence, sequence);
        Sleep(4);
    }
    ptr::write_volatile(&mut (*shared).buttons, 0);
    if !owner.is_null() {
        CloseHandle(owner);
    }
    UnmapViewOfFile(shared.cast());
    CloseHandle(mapping);
    FreeLibraryAndExitThread(MODULE, 0);
}

#[no_mangle]
pub unsafe extern "system" fn DllMain(module: Handle, reason: u32, _: *mut c_void) -> i32 {
    if reason == DLL_PROCESS_ATTACH {
        MODULE = module;
        DisableThreadLibraryCalls(module);
        let thread = CreateThread(
            ptr::null_mut(),
            0,
            input_thread,
            ptr::null_mut(),
            0,
            ptr::null_mut(),
        );
        if thread.is_null() {
            return 0;
        }
        CloseHandle(thread);
    }
    1
}
