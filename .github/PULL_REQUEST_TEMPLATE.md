## 概要

<!-- 说明问题、方案、用户可见结果，以及本 PR 明确不处理的内容。 -->

关联 Issue：

## 变更类型

- [ ] 缺陷修复
- [ ] 新功能
- [ ] 重构/性能
- [ ] 构建/验证
- [ ] 文档/治理
- [ ] 依赖/CI/发布

## 验证

<!-- 只勾选实际运行的项目，并附关键手工场景。 -->

- [ ] `dotnet restore .\BackdropForCodex.slnx`
- [ ] `dotnet build .\BackdropForCodex.slnx --configuration Release --no-restore`
- [ ] `dotnet format .\BackdropForCodex.slnx --verify-no-changes --no-restore`
- [ ] `dotnet publish .\src\BackdropForCodex.App\BackdropForCodex.App.csproj --configuration Release --runtime win-x64 --self-contained true --no-restore -p:PublishSingleFile=true`
- [ ] 已在 Windows 11 x64 + Store/MSIX Codex 上手工验证（若适用）

手工验证与未验证项：

## 安全、隐私与兼容性

- [ ] 未新增聊天读取、遥测、崩溃上传或项目自有远程服务。
- [ ] 所有监听与 CDP 端点仍严格限制为回环地址。
- [ ] 用户字符串未拼接为脚本、HTML、CSS、命令行或任意文件 URL。
- [ ] 日志、错误和诊断材料不包含聊天、令牌或真实绝对路径。
- [ ] 未修改/重签 Codex 包，未要求管理员权限。
- [ ] 若边界发生变化，已更新 `THREAT_MODEL.md`、`PRIVACY.md`、`SECURITY.md` 与公开验证说明。

## 依赖与发行材料

- [ ] 没有新增运行时依赖；或已说明必要性、许可证和维护风险。
- [ ] 已按需更新 `THIRD_PARTY_NOTICES.md`、README 与 `CHANGELOG.md`。

## 提交来源

- [ ] 每个 commit 都包含我本人有效的 DCO `Signed-off-by` 行。
- [ ] 本 PR 不含无关改动、生成目录、个人设置、真实媒体或秘密。
