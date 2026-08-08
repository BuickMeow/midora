# Midora 真实 SF2 非 UI Release Gate 矩阵（2026-08-08）

状态：全部通过

## 1. 门禁范围

每个 SF2 均独立执行根目录 `Test-NonUIRelease.ps1`，没有复用上一组的测试结果。每次执行均包含：

- 固定 win-x64 BASS/BASSMIDI/BASSWASAPI manifest、完整版本与 SHA-256 校验；
- locked restore；
- 6 个 solution 的 CI Release build；
- 当前源码的 win-x64 Native AOT Audio Worker publish；
- 10 个测试项目共 1053 项测试，要求 0 failure、0 skip；
- 真实 BASS/SF2 的 PCM、cache miss→publish→exact hit、命中路径零重复 synthesis、最大 256 个 1-channel Unit Stream、render 热路径 0 B 和 Native AOT 子进程集成门。

本矩阵是自动化技术验收，不包含人耳试听结论。

## 2. 输入与结果

| # | SF2 | 大小 | 结果 | Release Gate 证据 |
|---:|---|---:|---|---|
| 1 | `D:\Soundfonts\sf2\sDetrimental Concert Grand Piano.sf2` | 867,563,444 B（827.37 MiB） | 1053/1053；0 failure；0 skip | `artifacts/non-ui-release-gate-sf2-01-sdetrimental-20260808/` |
| 2 | `D:\Soundfonts\sf2\SGM-V2.01.sf2` | 247,406,594 B（235.95 MiB） | 1053/1053；0 failure；0 skip | `artifacts/non-ui-release-gate-sf2-02-sgm-20260808/` |
| 3 | `D:\Soundfonts\sf2\JV1080Ti.sf2` | 12,690,496 B（12.10 MiB） | 1053/1053；0 failure；0 skip | `artifacts/non-ui-release-gate-sf2-03-jv1080ti-20260808/` |
| 4 | `D:\Soundfonts\sf2\Ultima C7 Grand II.sf2` | 79,602,020 B（75.91 MiB） | 1053/1053；0 failure；0 skip | `artifacts/non-ui-release-gate-sf2-04-ultima-c7-20260808/` |
| 5 | `D:\Soundfonts\sf2\Z-Doc Acoustic Piano Fantasy Mode.sf2` | 659,137,140 B（628.60 MiB） | 1053/1053；0 failure；0 skip | `artifacts/non-ui-release-gate-sf2-05-zdoc-20260808/` |
| 6 | `D:\Soundfonts\sf2\Roland XP-80.sf2` | 1,740,708 B（1.66 MiB） | 1053/1053；0 failure；0 skip | `artifacts/non-ui-release-gate-sf2-06-roland-xp80-20260808/` |
| 7 | `D:\Soundfonts\sf2\Splendid_256.sf2` | 257,940,148 B（245.99 MiB） | 1053/1053；0 failure；0 skip | `artifacts/non-ui-release-gate-sf2-07-splendid-20260808/` |
| 8 | `D:\Soundfonts\sf2\minecraft.sf2` | 657,512 B（0.63 MiB） | 1053/1053；0 failure；0 skip | `artifacts/non-ui-release-gate-sf2-08-minecraft-20260808/` |

累计：8424/8424 tests passed，0 failure，0 skip；共生成并解析 80 个 TRX。

## 3. 单次 1053 项精确构成

| 测试项目 | 数量 |
|---|---:|
| Midora.Common.Tests | 68 |
| Midora.Compiler.Tests | 250 |
| Midora.MidiExport.Tests | 33 |
| Midora.Persistence.Tests | 94 |
| Midora.AudioRender.Tests | 36 |
| Midora.Application.Tests | 274 |
| Midora.Playback.Tests | 75 |
| Midora.Audio.Bass.Tests | 170 |
| Midora.AudioDevice.BassWasapi.Tests | 35 |
| Midora.Midi.Tests | 18 |
| 合计 | 1053 |

## 4. 证据完整性复核

- 八组各有 10 个 TRX；每组 counters 均为 total=1053、executed=1053、passed=1053、failed=0。
- 八套 `worker-win-x64/` 均包含 `Midora.Audio.Bass.Worker.exe`、`native-manifest.json`、`LICENSE` 和 `THIRD-PARTY-NOTICES.md`。
- 八套 Worker 目录均再次通过 `src/midora-audio/Test-BassNative.ps1 -Architecture win-x64` 的固定基线校验。
- 每次 Release build 均为 0 warning、0 error。
- BASS 二进制的正式再分发资格与发布当日许可文本核验仍是独立的发布所有者法律门，不属于本技术矩阵。
