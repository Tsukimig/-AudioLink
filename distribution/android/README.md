# 安卓接收端发行文件

`AudioLink-Android-1.4.0.apk` 为配套接收端的已签名发行文件，与 `android/app` 的 1.4.0 源码对应。Windows 1.4.3 继续使用此版本，二者版本号分别管理。

构建发布流程按 `manifest.json` 校验 SHA-256 后，将同一 APK 放入 Windows 便携包并作为 Release 独立附件提供。此目录不包含签名私钥、密码或本地设备设置。

更新安卓接收端时，应使用原签名密钥构建 APK，并同步更新清单和发布说明。更换签名密钥会影响覆盖安装。构建方法见 [安卓文档](../../android/README.md)。
