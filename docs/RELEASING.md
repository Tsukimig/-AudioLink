# 版本发布

Windows 版本由 `Directory.Build.props` 管理，安卓接收端版本由 `distribution/android/manifest.json` 管理。

1. 更新 Windows 版本号及 `docs/releases/v版本号.md`。
2. 如需更新手机接收端，使用原签名密钥生成 APK，替换 `distribution/android` 中的发行文件并更新版本号和 SHA-256。
3. 将变更推送至 `main`，或推送与 Windows 版本相同的 `v版本号` 标签；也可手动运行 Build and publish release。
4. 发布流程在 Windows 上构建自带运行环境的便携包，并核对 Windows 版本和配套 APK 校验值。
5. 文件全部上传成功后，草稿 Release 才转为正式 Release；已发布版本不会被同版本的后续运行覆盖。

每个 Release 包含版本化的 Windows ZIP、配套 APK、SHA256SUMS.txt 和记录源码提交的 release-manifest.json。APK 使用独立版本号。GitHub 的自动源码归档不替代运行文件。

签名私钥和密码保存在维护者自己的安全位置，不提交至仓库。发布工作流不运行实体音频、桌面交互或自动化测试；验证范围另行记录于 `VERIFICATION.md`。
