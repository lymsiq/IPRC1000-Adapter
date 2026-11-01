//! 窗口截图小工具（2026-10-31，仅供开发自测）
//!
//! 用途：改完界面后**自己看效果**，而不是让用户反复截图反馈。
//!
//! 用法：
//! ```text
//! shot.exe "<窗口标题>" <输出.png>
//! ```
//! 找不到窗口时返回非 0，方便脚本判断。
//!
//! 实现说明：用 GDI 的 BitBlt 抓**客户区**（不含标题栏），
//! 再 GetDIBits 转成 RGBA 交给 image 存 PNG。
//! 为什么不用 PrintWindow：本程序是 OpenGL 渲染（egui/glow），
//! PrintWindow 抓不到 GL 画面，会得到一张全黑或空白图 —— BitBlt 抓的是屏幕合成结果，可靠。

use windows::core::PCWSTR;
use windows::Win32::Foundation::{HWND, RECT};
use windows::Win32::Graphics::Gdi::{
    BitBlt, CreateCompatibleBitmap, CreateCompatibleDC, DeleteDC, DeleteObject, GetDC, GetDIBits,
    ReleaseDC, SelectObject, BITMAPINFO, BITMAPINFOHEADER, BI_RGB, DIB_RGB_COLORS, HGDIOBJ,
    SRCCOPY,
};
use windows::Win32::UI::WindowsAndMessaging::{FindWindowW, GetClientRect};

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let title = args.get(1).cloned().unwrap_or_else(|| "IPRC1000 原生内核".into());
    let out = args.get(2).cloned().unwrap_or_else(|| "shot.png".into());

    unsafe {
        let w: Vec<u16> = title.encode_utf16().chain(std::iter::once(0)).collect();
        let hwnd: HWND = FindWindowW(None, PCWSTR(w.as_ptr()));
        if hwnd.0 == 0 {
            eprintln!("找不到标题为「{title}」的窗口");
            std::process::exit(2);
        }

        let mut rect = RECT::default();
        if GetClientRect(hwnd, &mut rect).is_err() {
            eprintln!("GetClientRect 失败");
            std::process::exit(3);
        }
        let (cw, ch) = (rect.right - rect.left, rect.bottom - rect.top);
        if cw <= 0 || ch <= 0 {
            eprintln!("客户区尺寸为 0（窗口可能最小化了）：{cw}x{ch}");
            std::process::exit(4);
        }

        let hdc_win = GetDC(hwnd);
        let hdc_mem = CreateCompatibleDC(hdc_win);
        let hbm = CreateCompatibleBitmap(hdc_win, cw, ch);
        let old = SelectObject(hdc_mem, HGDIOBJ(hbm.0));
        let _ = BitBlt(hdc_mem, 0, 0, cw, ch, hdc_win, 0, 0, SRCCOPY);

        let mut bi = BITMAPINFO::default();
        bi.bmiHeader = BITMAPINFOHEADER {
            biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: cw,
            biHeight: -ch, // 负数 = 自上而下，省得后面再翻转
            biPlanes: 1,
            biBitCount: 32,
            biCompression: BI_RGB.0,
            ..Default::default()
        };

        let mut buf = vec![0u8; (cw * ch * 4) as usize];
        let lines = GetDIBits(
            hdc_mem,
            hbm,
            0,
            ch as u32,
            Some(buf.as_mut_ptr() as *mut std::ffi::c_void),
            &mut bi,
            DIB_RGB_COLORS,
        );
        if lines == 0 {
            eprintln!("GetDIBits 失败");
            std::process::exit(5);
        }

        // BGRA → RGBA（GDI 给的是 BGRX）
        for px in buf.chunks_exact_mut(4) {
            px.swap(0, 2);
            px[3] = 255;
        }

        let img = image::RgbaImage::from_raw(cw as u32, ch as u32, buf).expect("构造图像");
        if let Err(e) = img.save(&out) {
            eprintln!("保存失败：{e}");
            std::process::exit(6);
        }
        println!("已保存 {out}（{cw}x{ch}）");

        let _ = SelectObject(hdc_mem, old);
        let _ = DeleteObject(HGDIOBJ(hbm.0));
        let _ = DeleteDC(hdc_mem);
        ReleaseDC(hwnd, hdc_win);
    }
}
