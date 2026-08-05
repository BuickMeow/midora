# 第 6 章 SoundFont 与声音资源

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义 Project 级单一 SF2、内嵌与外部引用、资源校验、无 SF2 状态、Program/Bank 边界以及声音资源对播放和渲染的影响。

## 6.1 SoundFont Settings 系统级定义
每个 Project 必须包含一个 `SoundFont Settings` 顶层设置对象。
SoundFont Settings 是项目级声音资源设置，用于描述当前 Project 在播放、预览和音频渲染时应使用的 SF2 资源。
SoundFont Settings 可以处于以下状态之一：
```text
未选择 SF2
已选择外部引用 SF2 且当前可访问
已选择外部引用 SF2 但当前不可访问
已选择外部引用 SF2 但文件不可读
已选择外部引用 SF2 但格式不支持或无法加载
已选择内嵌 SF2 且当前可加载
已选择内嵌 SF2 但格式不支持或无法加载
```
SoundFont Settings 不应改变以下内容：
```text
Event Instrument 定义本身
Logical Track 内容
Segment 内容
Conductor Track 内容
Port / Channel / Channel Unit 分配语义
MIDI 导出事件语义
```
SoundFont Settings 只影响：
```text
播放
预览
音频渲染
相关状态提示
导出 Readme 中的推荐 SoundFont 信息
```
---
## 6.2 初版支持范围
初版只支持：
```text
SF2
```
初版不支持：
```text
SFZ
DLS
VSTi
LV2
Kontakt
SoundFont stack
多 SF2 层叠
每 Port 独立 SF2
每 Logical Track 独立 SF2
每 Event Instrument 独立 SF2
```
用户选择非 SF2 资源时，系统应阻止或标记为不支持。
具体文件扩展名检查、文件头检查、BASSMIDI 加载失败判断属于实现设计阶段。
---
## 6.3 项目级单一 SF2 规则
初版中，一个 Project 最多选择一个 SF2。
该 SF2 对项目中所有实际使用的 Midora Port 生效。
系统级规则：
```text
一个 Project 使用同一个 SF2
所有实际使用 Port 使用同一个 SF2
空闲 Port 不加载 SF2
每个 Port 不允许独立选择 SF2
每个 Logical Track 不允许独立选择 SF2
每个 Event Instrument 不允许独立选择 SF2
```
说明：
```text
“所有 Port 使用同一个 SF2”不等于 16 个 Port 都要常驻加载 SF2。
只有当前播放、预览或音频渲染上下文实际使用到的 Port 才需要对应 BASSMIDI Stream。
```
---
## 6.4 SF2 保存与引用模式
### 6.4.1 两种模式
初版支持两种 SoundFont 保存 / 引用模式：
```text
内嵌模式：将 SF2 复制进 .midora 项目文件 / 项目包
外部引用模式：引用项目目录附近的外部 SF2 文件
```
用户可以在选择 SF2 时决定采用哪种模式。
### 6.4.2 内嵌模式
内嵌模式下，系统将用户选择的 SF2 复制进 `.midora` 项目文件或项目包，使项目在声音资源上更接近自包含。
内嵌模式的系统级含义：
```text
项目分享时不必额外携带外部 SF2 文件
项目文件体积可能显著增大
项目保存和另存为可能更慢
该 SF2 仍然是项目级单一 SF2
所有实际使用 Port 仍使用同一个内嵌 SF2
```
当用户选择将 SF2 复制进项目文件时，系统应提醒用户：
```text
如果将项目文件分享给他人，需要注意该 SoundFont / 音色库拷贝行为可能涉及版权或授权问题。
```
该提醒属于信息提示，不表示 Midora 自动判断版权状态，也不表示 Midora 提供法律保证。
### 6.4.3 外部引用模式
外部引用模式下，初版只支持项目目录附近的相对路径引用。
允许引用的外部 SF2 位置仅限以下两类：
```text
与 .midora 项目文件同目录
.midora 项目文件所在目录下的 soundfonts\ 子目录
```
示例：
```text
ProjectFolder\Song.midora
ProjectFolder\MySoundFont.sf2
ProjectFolder\soundfonts\MySoundFont.sf2
```
初版不支持外部引用任意绝对路径。
初版不保存绝对路径 fallback。
如果用户选择的 SF2 不位于上述允许目录中，系统应阻止外部引用选择，或引导用户改用以下方式之一：
```text
复制进 .midora 项目文件 / 项目包
将 SF2 放到项目文件同目录
将 SF2 放到项目目录下的 soundfonts\ 子目录
```
具体 UI 文案和移动 / 复制辅助操作由 第 17～20 章的 UI 与交互规格 或实现设计阶段细化。
### 6.4.4 相对目录规则
为了简化初版资源管理，外部引用只读写上述两种相对目录。
系统保存外部引用时，应保存相对引用信息，而不是依赖本机绝对路径。
当项目文件被移动时，只要 SF2 仍与项目文件保持以下关系之一，项目即可继续找到外部 SF2：
```text
SF2 与 .midora 文件同目录
SF2 位于 .midora 文件所在目录下的 soundfonts\ 子目录
```
具体相对路径字段、大小写处理、同名文件冲突、跨平台路径分隔符兼容等推迟到 第 16 章《.midora 文件格式与持久化》 或实现设计。
---
## 6.5 SF2 选择、取消选择与替换
### 6.5.1 创建项目时选择 SF2
创建 Project 时允许用户选择 SF2，也允许不选择 SF2。
不选择 SF2 的 Project 仍然是有效 Project。
### 6.5.2 选择 SF2
用户选择 SF2 时，系统应立即验证：
```text
文件存在
文件可读
文件可作为 Midora 初版支持的 SF2 加载
```
验证失败时，不应接受该选择。
选择 SF2 时还应确定使用模式：
```text
复制进项目文件 / 项目包
外部相对引用
```
选择 SF2 属于项目可撤销编辑行为，并应使 Project 进入已修改状态。
### 6.5.3 取消选择 SF2
用户允许将 Project 从“已选择 SF2”状态改回“未选择 SF2”状态。
取消选择 SF2 后：
```text
Project 仍可保存
Project 仍可编译
Project 仍可 MIDI 导出
播放、预览、音频渲染不可用
现有 Event Instrument / Logical Track / Segment 内容不被删除
现有 Program Change / Bank Select 等事件不被删除
所有相关播放 / 预览 / 渲染缓存立即失效
```
取消选择 SF2 属于项目可撤销编辑行为，并应使 Project 进入已修改状态。
### 6.5.4 替换 SF2
用户允许替换当前 Project 的 SF2。
替换 SF2 后：
```text
Project 数据内容不自动重写
Event Instrument 中的 Program / Bank / CC / RPN / NRPN 等 MIDI 事件不自动修改
逻辑轨道内容不自动修改
编译资源分配语义不改变
播放、预览、音频渲染结果可能改变
所有相关播放 / 预览 / 渲染缓存立即失效
```
替换 SF2 属于项目可撤销编辑行为，并应使 Project 进入已修改状态。
---
## 6.6 SF2 缺失、不可读、格式不支持
### 6.6.1 打开 Project 时外部 SF2 缺失
如果 Project 记录了外部引用 SF2，但打开 Project 时该 SF2 不存在或路径失效，系统仍允许打开 Project，并将 SoundFont Settings 标记为资源缺失状态。
此时：
```text
Project 打开不应失败
Project 可保存
Project 可编译
Project 可 MIDI 导出
播放、预览、音频渲染不可用
状态栏或 SoundFont Settings 中应提示 SF2 缺失
```
### 6.6.2 SF2 文件不可读
如果外部 SF2 文件存在但不可读，例如权限不足、文件被占用或读入失败，行为与 SF2 缺失一致：
```text
Project 不因该问题打开失败
播放、预览、音频渲染不可用
系统提供可定位的诊断信息或状态提示
```
### 6.6.3 SF2 格式不支持或加载失败
如果文件扩展名为 SF2，但实际无法作为有效 SF2 加载：
```text
Project 不因该问题打开失败
SoundFont Settings 标记为加载失败
播放、预览、音频渲染不可用
编译与 MIDI 导出不受影响
```
该规则同时适用于外部引用 SF2 与内嵌 SF2。
具体如何区分“格式不支持”“文件损坏”“BASSMIDI 加载失败”由 第 13 章《播放与预览》、第 15 章《音频文件渲染》 或实现设计阶段细化。
---
## 6.7 SF2 文件哈希与变化检测
### 6.7.1 哈希记录
初版应记录当前 SF2 的完整文件哈希。
该规则适用于：
```text
外部引用 SF2
内嵌 SF2
```
对于外部引用 SF2，哈希用于判断外部文件是否在项目保存后被替换或修改。
对于内嵌 SF2，哈希可用于完整性检查、导出 Readme 摘要或后续诊断。
具体哈希算法、计算时机、缓存策略、进度显示和性能优化属于 第 16 章《.midora 文件格式与持久化》 / 第 17～20 章的 UI 与交互规格 或实现设计阶段。
### 6.7.2 哈希不同时的行为
当系统发现当前外部 SF2 文件哈希与项目记录不一致时：
```text
给出警告
继续尝试加载当前文件
不阻止 Project 打开
不阻止编译
不阻止 MIDI 导出
如果加载成功，则播放、预览、音频渲染仍可用
如果加载失败，则按 SF2 加载失败处理
```
哈希不同表示声音结果可能已经变化，但不等同于项目结构损坏。
### 6.7.3 被动发现变化与项目修改状态
被动发现 SF2 缺失、不可读、加载失败或哈希变化时，不应标记 Project 已修改。
只有用户主动执行以下操作时，才应使 Project 进入已修改状态：
```text
选择 SF2
取消选择 SF2
替换 SF2
切换内嵌 / 外部引用模式
重新定位或重新绑定 SF2
```
外部文件变化是项目外部状态变化，不是用户对 Project 数据的编辑。
---
## 6.8 无 SF2 状态下的功能边界
无 SF2 状态包括：
```text
未选择 SF2
已选择但外部 SF2 缺失
已选择但外部 SF2 不可读
已选择但 SF2 无法加载
```
无 SF2 状态下允许：
```text
保存 Project
另存为 Project
编辑 Project Metadata
编辑 Conductor Track
创建 / 编辑 Event Instrument
创建 / 编辑 Logical Track
创建 / 编辑 Segment
编译
查看编译诊断
MIDI 导出
```
无 SF2 状态下禁止或不可用：
```text
整曲播放
中途播放
Event Instrument 预览 / 试听
Logical Track 预览
Segment 预览
音频渲染
任何需要 BASSMIDI 发声结果的功能
```
无 SF2 状态不进入编译诊断。
无 SF2 状态仅在状态栏、SoundFont Settings 面板或相关功能入口处提示。
无 SF2 状态下，MIDI 导出不应因为无 SF2 额外弹出导出前警告。
---
## 6.9 SoundFont 与编译的关系
SoundFont 不改变 Midora 的 MIDI 编译语义。
编译器不应因为无 SF2 而改变以下结果：
```text
Event Instrument Instance 展开
Note → Event 映射
C# 映射运行
生命周期计算
Overlap 策略
Reset 插入
Channel Group 分配
Port / Channel / Channel Unit 分配
MIDI 事件流生成
Conductor Track 合并
MIDI 导出结构
```
如果 Project 无 SF2，编译仍可成功。
如果编译本身存在其他错误，例如 Channel Unit 不足、C# 映射编译失败、非法事件值，则仍应按对应系统规则失败。
无 SF2 状态不作为编译诊断项。
---
## 6.10 SoundFont 与播放、预览、音频渲染的关系
播放、预览和音频渲染需要有效 SF2。
当 Project 处于有效 SF2 状态时，播放系统应在实际需要发声的上下文中使用该 SF2。
系统级规则：
```text
每个实际使用的 Midora Port 对应一个 BASSMIDI Stream
所有实际使用 Port 使用同一个 SF2
空闲 Port 不创建 BASSMIDI Stream
空闲 Port 不加载 SF2
所有实际使用 Port 的 Channel 10 初始化为 melodic
所有正式 Stream 启用 BASS_MIDI_NOFX
实时 Stream 按输出设备实际采样率创建，文件渲染 Stream 按本次文件采样率创建
选择、替换、取消 SF2 或发现影响声音结果的 SF2 变化后，相关播放 / 预览 / 渲染缓存立即失效
```
无 SF2 或 SF2 加载失败时，音频渲染功能入口应可见但禁用，并显示不可用原因。
播放与预览入口也应能表达类似的不可用状态，具体 UI 呈现由第 17～20 章规定。
以下内容不在本章定义：
```text
BASSMIDI Stream 何时创建
BASSMIDI Stream 何时销毁
SF2 加载句柄是否共享
是否复用已加载 SF2
播放 buffer 如何失效
混音如何处理
加载失败如何回滚
```
这些由 第 13 章《播放与预览》 和实现设计阶段细化。
---
## 6.11 SoundFont 与 MIDI 导出的关系
MIDI 导出不依赖 SF2 加载状态。
无 SF2 时仍允许 MIDI 导出。
MIDI 导出可以在 Readme 中记录推荐 SoundFont 信息，但导出的 MIDI 事件本身仍应基于 Event Instrument、Logical Track、Conductor Track、Reset、Port / Channel 分配等规则生成。
Readme 中的 SoundFont 信息应记录：
```text
推荐 SF2 文件名
导出时 SoundFont 可用状态
所有 Port 使用同一个 SF2 的说明
Midora 不假设 GM 的说明
Program 编号含义取决于所用 SF2 的说明
```
Readme 不应默认记录用户本机绝对路径。
如果 Project 无有效 SF2，Readme 可将 SoundFont 状态标记为：
```text
未指定
缺失
不可读
加载失败
哈希变化但已加载
```
具体 Readme 字段、格式、文件名和导出目录结构由第 14 章《MIDI 导出》规定。
---
## 6.12 Program / Bank 与 SF2 的关系边界
### 6.12.1 Program 编号显示
初版 UI 只显示 Program 编号。
用户看到：
```text
Program 1–128
```
MIDI 内部值为：
```text
Program Change value 0–127
```
初版不显示 GM 乐器名。
初版不显示 SF2 内部 preset / instrument 名称。
初版不应默认：
```text
Program 1 = Acoustic Grand Piano
Program 33 = Acoustic Bass
Channel 10 = Drum Kit
```
### 6.12.2 Program Change 与 SF2
Program Change 是 MIDI 事件。
它属于 Event Instrument 事件内容，不属于 SoundFont Settings。
SoundFont Settings 只决定播放、预览或音频渲染时这些 MIDI Program / Bank 指向的实际声音资源。
替换 SF2 不应自动修改已有 Program Change。
### 6.12.3 Bank Select 与 SF2
Bank Select 也属于 MIDI 事件语义，不属于 SoundFont Settings。
初版不根据当前 SF2 校验 Bank Select / Program Change 是否对应真实存在的 preset。
编译阶段只校验：
```text
MIDI 值范围
Midora 事件语义
事件类型合法性
```
不校验：
```text
当前 SF2 是否存在该 Bank / Program
该 Program 是否实际有声音
该 preset 是否符合 GM 语义
```
原因：
```text
Midora 不假设 GM
初版 Program 名称不显示
SF2 preset 解析属于更细层实现
不同播放器 / 合成器对缺失 Bank / Program fallback 行为可能不同
声音资源细节不应反向绑定 MIDI 编译语义
```
---
## 6.13 非 GM SF2 提示原则
Midora 不假设当前 SF2 是 GM 标准音色库。
严格地说，系统通常无法仅凭“文件是 SF2”可靠判断其是否完全符合 GM 编号语义。
因此初版采用通用信息提示，而不是对“非 GM”进行强检测：
```text
Program 编号含义由当前 SF2 决定。
Midora 初版不显示具体乐器名。
Midora 不假设当前 SF2 符合 GM。
```
该提示应在 SoundFont Settings 面板常驻显示。
不应每次选择 SF2、每次打开 Project 或每次导出都弹窗提示。
导出 Readme 可包含相关说明，但不应把它作为阻塞性警告。
---
## 6.14 错误、警告与信息诊断
### 6.14.1 错误 / 操作失败
以下情况应导致对应操作失败：
```text
无有效 SF2 时尝试播放
无有效 SF2 时尝试预览
无有效 SF2 时尝试音频渲染
选择的文件不是支持的 SF2 或无法加载为 SF2
播放 / 预览 / 渲染过程中 SF2 加载失败
实际需要的 BASSMIDI Stream 无法加载同一 SF2
用户尝试在外部引用模式下引用非允许相对目录中的 SF2
```
这些失败不应自动导致 Project 打开失败，除非项目结构本身损坏。
### 6.14.2 警告
以下情况可作为警告或后续由第 15 章《音频文件渲染》规定的诊断来源：
```text
Project 指定的外部 SF2 路径失效
Project 指定的外部 SF2 文件不可读
Project 指定的外部 SF2 文件哈希与项目记录不一致
导出时推荐 SF2 不可访问
内嵌 SF2 无法加载
```
哈希不同的处理原则是：
```text
警告，然后继续尝试加载。
```
### 6.14.3 信息
以下情况可作为信息或 UI 状态提示：
```text
Project 未指定 SF2，因此播放、预览和音频渲染不可用
无 SF2 不影响 MIDI 编译和 MIDI 导出
Midora 初版不显示 Program 名称，只显示 Program 编号
Program 编号含义取决于当前 SF2
Midora 不假设当前 SF2 是 GM 音色库
所有实际使用 Port 应使用同一个 SF2
复制 SF2 进项目文件后，分享项目时应注意 SoundFont 授权 / 版权问题
```
---
## 6.15 规则、限制与失败条件
### 6.15.1 强制规则
1. 初版只支持 SF2。
2. 初版一个 Project 最多选择一个 SF2。
3. 所有实际使用 Port 使用同一个 SF2。
4. 空闲 Port 不创建 BASSMIDI Stream，也不加载 SF2。
5. 每 Port 不支持独立 SF2。
6. 每 Logical Track 不支持独立 SF2。
7. 每 Event Instrument 不支持独立 SF2。
8. 创建 Project 时可以不选择 SF2。
9. 无 SF2 状态下不创建 BASSMIDI 实例。
10. 无 SF2 状态下不能播放、预览或音频渲染。
11. 无 SF2 状态下允许保存、编译和 MIDI 导出。
12. 无 SF2 状态不进入编译诊断。
13. 无 SF2 状态仅在状态栏、SoundFont Settings 或相关功能入口中提示。
14. 无 SF2 状态下 MIDI 导出不需要因为无 SF2 额外弹出导出前警告。
15. 用户选择 SF2 时应立即验证文件存在、可读并可作为 SF2 加载。
16. 用户允许取消选择 SF2。
17. 用户允许替换 SF2。
18. 选择、取消、替换 SF2 属于项目可撤销编辑行为。
19. 选择、取消、替换 SF2 应使 Project 进入已修改状态。
20. 被动发现 SF2 缺失、不可读、加载失败或哈希变化，不标记 Project 已修改。
21. 初版支持两种 SoundFont 模式：内嵌复制进项目文件 / 项目包，或外部相对引用。
22. 将 SF2 复制进项目文件时，应提示用户分享项目时注意 SoundFont 授权 / 版权问题。
23. 外部引用 SF2 只支持与项目文件同目录，或项目目录下的 `soundfonts\` 子目录。
24. 初版不保存任意绝对路径 fallback。
25. 外部引用超出允许目录时，应阻止或引导用户移动 / 复制 SF2。
26. 初版应记录 SF2 文件哈希。
27. 外部 SF2 哈希不同时，应警告，然后继续尝试加载。
28. 打开项目时 SF2 缺失、不可读或无法加载，Project 仍允许打开。
29. SF2 异常时，播放、预览、音频渲染不可用。
30. 替换、取消 SF2 或发现影响声音结果的 SF2 变化后，相关播放 / 预览 / 渲染缓存立即失效。
31. Midora 不假设 SF2 是 GM 标准音色库。
32. SoundFont Settings 面板应常驻提示：Midora 不假设 GM，Program 含义由 SF2 决定。
33. 初版只显示 Program 编号，不显示 GM 名称。
34. 初版不显示 SF2 内部 preset / instrument 名称。
35. 初版不根据当前 SF2 校验 Bank Select / Program Change 是否存在。
36. 编译阶段只校验 MIDI 值范围和 Midora 事件语义。
37. 音频渲染功能在无 SF2 或 SF2 加载失败时，入口可见但禁用，并显示原因。
38. MIDI 导出 Readme 应记录推荐 SF2 文件名、可用状态，以及所有 Port 使用同一 SF2 的说明。
### 6.15.2 警告情况
以下情况应产生 SoundFont 相关警告或诊断入口：
```text
Project 指定的外部 SF2 路径失效
Project 指定的外部 SF2 文件不可读
Project 指定的外部 SF2 文件哈希与项目记录不一致
外部 SF2 哈希不同但仍可加载，声音结果可能变化
导出时推荐 SF2 不可访问
内嵌 SF2 无法加载
```
具体警告等级、文案、定位和 UI 呈现由第 15 章《音频文件渲染》规定。
### 6.15.3 信息情况
以下情况应作为信息或状态提示：
```text
Project 未指定 SF2
无 SF2 不影响 MIDI 编译和 MIDI 导出
Midora 初版只显示 Program 编号
Midora 不假设当前 SF2 是 GM 音色库
Program 编号含义取决于当前 SF2
所有实际使用 Port 应使用同一个 SF2
复制 SF2 进项目文件后，分享项目时应注意音色库授权 / 版权问题
```
### 6.15.4 失败条件
以下情况应导致相应操作失败或被阻止：
| 场景 | 结果 |
|---|---|
| 用户选择的文件不存在、不可读或无法作为 SF2 加载 | 选择失败 |
| 用户在外部引用模式下选择非允许相对目录中的 SF2 | 阻止外部引用，或引导复制 / 移动 |
| 无有效 SF2 时尝试播放 | 播放失败或阻止播放 |
| 无有效 SF2 时尝试预览 | 预览失败或阻止预览 |
| 无有效 SF2 时尝试音频渲染 | 渲染失败或阻止渲染 |
| 播放 / 预览 / 渲染过程中 SF2 加载失败 | 对应操作失败 |
| 实际使用的 BASSMIDI Stream 无法加载同一 SF2 | 播放 / 预览 / 渲染失败 |
| 用户尝试为不同 Port / Logical Track / Event Instrument 设置不同 SF2 | 阻止操作 |
| 用户尝试选择非 SF2 声音资源格式 | 阻止操作或标记不支持 |
---
