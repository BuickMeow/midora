# Midora 发布检查清单

## 1. 版本冻结

- [ ] `eng/Version.props` 是唯一产品版本源。
- [ ] 开发基线为 `1.0.0-dev`；RC 使用 `1.0.0-rc.N`；正式版清空 suffix 得到 `1.0.0`。
- [ ] 运行 `./Test-VersionControl.ps1 -ExpectedVersion <version>`。
- [ ] EXE 的 Product/File/Informational Version 与预期一致。
- [ ] Project manifest 和 MIDI Export Readme 使用同一 Informational Version。
- [ ] `CHANGELOG.md` 已把 Unreleased 内容归档到本次版本和发布日期。

## 2. Project Format 兼容

- [ ] `PersistenceContractV1.FileFormatVersion == 1`，且没有原地改变 Format 1 schema/field semantics。
- [ ] JSON schema set SHA-256、protobuf descriptor hash 与代表性 golden wire bytes 全部通过。
- [ ] 1.0.0-dev 期间保存的真实完整作品能够由候选版本打开、编译、编辑、保存副本并再次打开。
- [ ] 保存副本后源文件未被修改；普通保存仍保持备份、校验和原子发布。
- [ ] 若引入 Format 2，存在显式 V1 reader、detached migration、升级提示和旧文件 golden 回归；没有修改 V1 codec 冒充兼容。
- [ ] 未来格式和未知 schema 在 Project 提交前明确拒绝。

## 3. 自动验证

- [ ] locked restore、warnings-as-errors、deterministic Release build 通过。
- [ ] Persistence、Compiler、Application、MIDI Import/Export、Playback、Audio Render 和 Desktop tests 全通过且无意外 Skip。
- [ ] `Test-NonUIRelease.ps1` 使用固定 .NET SDK、BASS baseline 和真实 SF2 通过。
- [ ] Native AOT `win-x64` Worker 与主程序版本/协议匹配。
- [ ] 大型 MIDI、完整 Midora 作品、连续播放/停止、Mute/Solo、预览、MIDI Export 与 Audio Render 完成人工验收。

## 4. 分发与许可

- [ ] 根 MIT `LICENSE` 和 `THIRD-PARTY-NOTICES.md` 随产物发布。
- [ ] Sora、JetBrains Mono、Fluent System Icons 等 notices/许可证完整。
- [ ] BASS/BASSMIDI/BASSWASAPI 版本和 SHA-256 匹配固定 baseline。
- [ ] 发布主体、收入方式、渠道和发布日 BASS 条款已经重新核验；未满足时不分发 BASS DLL。
- [ ] 正式 ZIP/安装产物生成 SHA-256，且从空目录完成一次启动/项目重开验证。

## 5. GitHub Release

- [ ] 使用 `./Publish-MidoraLocal.ps1` 生成并人工检查本地 self-contained `win-x64` 目录、ZIP、单层 `midora/` 结构和 SHA-256；正式候选不得带 `-dirty`。
- [ ] 运行 `git status --short`，工作区干净。
- [ ] 创建 annotated tag：`git tag -a v<version> -m "Midora <version>"`。
- [ ] 运行 `./Test-VersionControl.ps1 -ExpectedVersion <version> -RequireClean -RequireTagAtHead`。
- [ ] 推送 commit 和 tag；不得移动或复用已发布 tag。
- [ ] GitHub Release 附带产物、SHA-256、Release Notes、兼容说明和已知问题。
