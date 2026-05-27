# Giantess LLM Mod

[English](#english) | [中文](#中文)

---

`<a name="english"></a>`

## English

**Giantess LLM Mod** is a BepInEx plugin that adds AI to "Giantess Sandbox" by integrating Large Language Models (LLM). This mod allows the GTS to have intelligent dialogues, express a wide range of emotions, and perform context-aware actions based on real-time game states.

### 🌟 Key Features

* **Decision System:** Powered by OpenAI-compatible APIs (GPT-4, Claude, or local LLMs like Llama 3).
* **Context Awareness:** The AI understands the situation, including:
  * **Player Status:** Health, position, and whether they are being held, in the mouth, or in the stomach.
  * **GTS Internal State:** Hunger, horniness, stomach activity, acid levels, and burp buildup.
  * **Memory & Relationship:** Tracks how the GTS feels about you, your past interactions, and current interest levels.
  * **Event Tracking:** Remembers recent significant events (e.g., being swallowed, escaping, being teased).
* **Dynamic Visual Emotions:** Maps AI emotions to in-game facial expressions (eyes and mouth) using an emotion mapping system.
* **Action System:** A whitelist of actions (walking, picking up, swallowing, patting stomach, etc.) that the AI can choose to perform.
* **In-Game Overlay UI:** A custom UI for:
  * Chatting with the GTS directly.
  * Monitoring real-time game state data.
  * Configuring API settings and prompts.
  * Manual triggering of AI responses.
* **Proactive Interactions:** Configurable automatic triggers based on time or specific in-game events.
* **Reflection-Powered:** Uses advanced reflection to interface with game internals, allowing for flexible action execution.

### 🛠️ Installation

1. Ensure you have **BepInEx 5** installed in your game directory.
2. Download the latest release and place the `GiantessLLMMod.dll` into the `BepInEx/plugins` folder.
3. Ensure `Newtonsoft.Json.dll` is available (usually comes with BepInEx or the game).
4. Launch the game once to generate the configuration file.

### ⚙️ Configuration

Press the **F10** key (default) in-game to open the Mod Overlay.

* **API Settings:** Set your OpenAI-compatible `ApiBaseUrl`, `ApiKey`, and `ModelName`.
* **Triggers:** Enable/Disable timed triggers or event-based triggers.
* **Prompts:** Customize the system prompt to define the personality of the giantesses.

### ⌨️ Keybinds (Default)

* **F10:** Toggle Mod Overlay UI.
* **F9:** Manually trigger an AI response.
* **F8:** Run Reflection Probe (Developer tool to scan game classes).

---

`<a name="中文"></a>`

## 中文

**Giantess LLM Mod** 是一个 BepInEx 插件，通过集成大语言模型（LLM）为“Giantess Sandbox”接入AI。该模组让GTS能够进行智能对话，表达丰富的情感，并根据实时的游戏状态执行符合情境的动作。

### 🌟 核心功能

* **决策系统：** 由 OpenAI 兼容接口（如 GPT-4、Claude 或本地 Llama 3 模型）驱动。
* **情境感知：** AI 能够理解当前处境，包括：
  * **玩家状态：** 生命值、位置，以及是否被抓住、在口中或在胃里。
  * **GTS内在状态：** 饥饿值、性致、胃部活动频率、胃酸水平以及饱嗝积攒程度。
  * **记忆与关系：** 追踪女巨人对你的感觉、过往互动经历以及当前的兴趣水平。
  * **事件追踪：** 记录近期发生的重大事件（如：被吞下、逃脱、被调戏）。
* **动态视觉情感：** 通过情感映射系统，将 AI 的情绪实时反映在游戏内的面部表情（眼睛和嘴巴）上。
* **动作系统：** AI 可以从动作白名单中自主选择执行（行走、抓取、吞咽、摸肚子等）。
* **游戏内悬浮 UI：** 自定义界面用于：
  * 直接与GTS聊天。
  * 实时监控游戏状态数据。
  * 配置 API 设置和提示词 (Prompt)。
  * 手动触发 AI 响应。
* **主动交互：** 可配置的基于时间或特定游戏事件的自动触发机制。
* **反射驱动：** 利用先进的反射技术与游戏底层交互，实现灵活的动作控制。

### 🛠️ 安装步骤

1. 确保你的游戏目录已安装 **BepInEx 5**。
2. 下载最新版本，并将 `GiantessLLMMod.dll` 放入 `BepInEx/plugins` 文件夹中。
3. 确保 `Newtonsoft.Json.dll` 存在（通常 BepInEx 或游戏自带）。
4. 启动一次游戏以生成配置文件。

### ⚙️ 配置说明

在游戏中按下 **F10** 键（默认）打开模组悬浮窗。

* **API 设置：** 设置你的 OpenAI 兼容 `ApiBaseUrl` (接口地址)、`ApiKey` (密钥) 和 `ModelName` (模型名称)。
* **触发器：** 开启/关闭定时触发或事件驱动触发。
* **提示词：** 自定义系统提示词 (System Prompt) 以定义女巨人的个性。

### ⌨️ 快捷键（默认）

* **F10:** 切换模组悬浮 UI 显示。
* **F9:** 手动触发 AI 响应。
* **F8:** 运行反射探测（开发者工具，用于扫描游戏类名）。

---

### ⚠️ Disclaimer / 免责声明

This mod is intended for use with "Giantess Sandbox". Please use it responsibly and in accordance with the game's community guidelines.
本模组仅供在 "Giantess Sandbox" 中使用。请负责任地使用，并遵守游戏社区准则。
