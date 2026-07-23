# 第三方软件声明

最后人工核对：2026-07-23

Backdrop for Codex 依据 Apache License 2.0 发布，但它依赖或随发布物包含由其他作者提供的软件。第三方组件仍受各自许可证和声明约束。本文件是便于阅读的基线；具体发行版中直接、传递、运行时及相关构建组件的机器可读清单是随该发行版发布的 SPDX SBOM，发行 ZIP 同时保留适用的 WPF-UI、WPF-UI.Tray、CommunityToolkit.Mvvm、.NET/ASP.NET Core/Windows Desktop 上游许可证与第三方 notice。

## 运行时直接依赖

| 组件 | 当前直接版本/来源 | 用途 | 上游许可证 |
| --- | --- | --- | --- |
| WPF-UI | 4.3.0 | Fluent 控件、主题、Mica 与窗口基础 | MIT |
| WPF-UI.Tray | 4.3.0 | 纯 WPF 通知区域图标 | MIT |
| CommunityToolkit.Mvvm | 8.4.2 | ViewModel、可观察属性与命令基础设施 | MIT |
| PuppeteerSharp | 25.3.4 | CDP 客户端 | MIT |
| WebDriverBiDi | 0.0.54（传递依赖） | 浏览器协议模型 | MIT |
| Microsoft.IO.RecyclableMemoryStream | 3.0.1（传递依赖） | PuppeteerSharp 缓冲区 | MIT |
| .NET Runtime / Windows Desktop | .NET 10，self-contained `win-x64` | 托管运行时与 WPF | MIT 及其上游第三方声明 |
| ASP.NET Core shared framework | .NET 10 | 回环媒体服务 | MIT 及其上游第三方声明 |

上游项目：

- WPF-UI / WPF-UI.Tray: <https://github.com/lepoco/wpfui>
- CommunityToolkit.Mvvm: <https://github.com/CommunityToolkit/dotnet>
- PuppeteerSharp: <https://github.com/hardkoded/puppeteer-sharp>
- WebDriverBiDi.NET: <https://github.com/webdriverbidi-net/webdriverbidi-net>
- Microsoft.IO.RecyclableMemoryStream: <https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream>
- .NET Runtime、WPF、ASP.NET Core: <https://github.com/dotnet>

Windows API、媒体栈、Microsoft Store/MSIX 与官方 Codex 由用户系统或独立产品提供，不因本项目而重新许可或成为本项目的一部分。

## 构建与发布工具

下列工具用于生成发行材料，正常发行 ZIP 不包含其工具程序集：

| 组件 | 当前直接版本 | 上游许可证 |
| --- | --- | --- |
| Microsoft.Sbom.DotNetTool | 4.1.5 | MIT |

## 治理文档

`CODE_OF_CONDUCT.md` 改编自 Contributor Covenant 2.1，并依照 Creative Commons Attribution 4.0 International 使用；原始文本与署名链接见该文件。此文档许可不改变项目代码的 Apache-2.0 许可。

## MIT License（适用于上表标注 MIT 的组件）

各组件版权归其各自作者和贡献者所有。

> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all
> copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.

Apache-2.0 的完整文本见 [LICENSE](LICENSE)。具体第三方版权名称、传递依赖、原生库和许可证文件应以对应 NuGet 包、上游源码及发行版 SBOM 为准。

## 维护要求

新增、升级或移除依赖时，贡献者必须核对许可证兼容性、更新本表，并确保发布流程的 SBOM 反映实际输出。发现遗漏或不准确声明时，请提交普通 Issue；若遗漏导致安全或敏感供应链影响，请按 [SECURITY.md](SECURITY.md) 私下报告。
