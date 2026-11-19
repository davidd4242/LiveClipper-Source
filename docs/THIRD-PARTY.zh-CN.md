# 许可边界与来源

仓库根目录的 [MIT 许可](../LICENSE) 仅适用于项目第一方代码及随附第一方文档（版权署名：David AY）。第三方代码、依赖、上游标志、外部运行服务和独立下载的二进制不由该许可重新授权。

- installer/licenses/ 保留 1.3.1 发行构建的组件通知，包括 NuGet / WebView2、uv、React 等；版本依据 web/package-lock.json、项目与锁文件、installer/dependencies.json。这些依赖并非都内置于此源码树。
- src/LiveClipper.App/wwwroot/lib/hls.min.js.LICENSE.txt 是发行附带的简短提示，不包含 hls.js 本体或完整 Apache 文本。应从 hls.js@1.5.20 原包恢复；分发该文件时请随附上游发行包的许可/通知。
- **编辑器/嵌入编解码器：**本仓不存放原版生成/衍生运行文件。组件对应关系见 [编辑器通知](../installer/licenses/EDITOR-CODEC-NOTICES.md)，含 FilmCraft（包括 ArtCraft Team 与 FilmCraft contributors 的原始声明）、craft-media、Kvazaar、thiserror、Rust 标准库、Emscripten 与 musl。上游通知原样保留，涵盖的某些资源不在本工程内。
- 原六枚未列出再分发条款的 Iconfont 图标、原产品图标/徽标与 favicon 均不包含。此树使用单独绘制的中性几何占位图。
- 可执行文件、编辑器中嵌入的二进制、FFmpeg、模型、WebView2 运行时及云服务不随本源码一并授权。在自行发布完整安装包之前应核对实际打包内容的原始条款，勿把发行组件的说明当作项目总许可。
