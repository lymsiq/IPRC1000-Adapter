// 嵌入 manifest（Common Controls v6 + DPI 感知）与图标资源
fn main() {
    let manifest = embed_manifest::new_manifest("Iprc1000Native");
    if let Err(e) = embed_manifest::embed_manifest(manifest) {
        println!("cargo:warning=manifest 嵌入失败：{e}");
    }
    // 图标：没有它，exe 在资源管理器里是白纸图标（用户反馈"软件图标没有"）
    if std::path::Path::new("app.rc").exists() {
        // 注意：这个版本的 embed_resource::compile 返回 ()；失败时它自己会 panic 并给出原因
        embed_resource::compile("app.rc", embed_resource::NONE);
    }
    println!("cargo:rerun-if-changed=app.rc");
    println!("cargo:rerun-if-changed=icon.ico");
}
