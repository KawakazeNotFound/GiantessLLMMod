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

Press the **F8** key (default) in-game to open the Mod Overlay.

* **API Settings:** Set your OpenAI-compatible `ApiBaseUrl`, `ApiKey`, and `ModelName`.
  * **To use standard OpenAI format:** Set URL to an endpoint like `http://127.0.0.1:11434/v1/chat/completions`.
  * **To use Ollama native format:** Set URL ending with `/api/generate` (e.g., `http://127.0.0.1:11434/api/generate`). The mod will auto-detect this and switch to Ollama's native prompt format.
* **Triggers:** Enable/Disable timed triggers or event-based triggers.
* **Prompts:** Customize the system prompt to define the personality of the giantesses.

### ⌨️ Keybinds (Default)

* **F8:** Toggle Mod Overlay UI.
* **F7:** Manually trigger an AI response.
* **F9:** Run Reflection Probe (Developer tool to scan game classes).

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

### 🛠️ 详细安装教程

#### 第一步：安装 BepInEx (插件运行框架)
如果你已经安装过 BepInEx，请直接跳到第二步。
1.  **下载：** 前往 [BepInEx GitHub 发布页](https://github.com/BepInEx/BepInEx/releases)，下载 **BepInEx_x64_5.4.x.x.zip** (适用于 64 位游戏)。
2.  **找到游戏文件夹：** 在 Steam 中右键点击 "Giantess Sandbox"，选择 **管理** > **浏览本地文件**。
3.  **解压：** 打开下载好的 `.zip` 压缩包，将里面 **所有的文件和文件夹** 全部拖进你的游戏根目录（即包含 `Giantess Sandbox.exe` 的那个文件夹）。
4.  **激活：** 运行一次游戏。看到游戏主界面后直接退出即可。此时游戏目录下会自动生成 `BepInEx/plugins` 等文件夹。

#### 第二步：安装本模组
1.  **下载：** 下载本模组的最新压缩包。
2.  **放置文件：**
    *   进入游戏目录下的 `BepInEx/plugins` 文件夹。
    *   将 `GiantessLLMMod.dll` 放入 `plugins` 文件夹内。
    *   确保文件夹内（或游戏自带库里）有 `Newtonsoft.Json.dll` 文件。
3.  **检查：** 启动游戏。如果安装成功，在游戏中按下 **F8** 键会弹出模组的悬浮窗口。

#### 第三步：配置 AI 接口
模组需要连接到一个“AI 大脑”才能工作。
1.  获取一个 API 密钥 (API Key)。你可以使用 OpenAI、DeepSeek，或者用 LM Studio 在本地运行模型。
2.  在游戏中按下 **F8** 打开模组窗口。
3.  点击 **Settings (设置)**，输入你的 **API Base URL (接口地址)** 和 **API Key (密钥)**。
4.  点击保存后，就可以开始在聊天框里和 GTS 互动了。

### ⚙️ 配置说明

在游戏中按下 **F8** 键（默认）打开模组悬浮窗。

* **API 设置：** 设置你的 OpenAI 兼容 `ApiBaseUrl` (接口地址)、`ApiKey` (密钥) 和 `ModelName` (模型名称)。
  * **使用标准 OpenAI 格式：** 将接口地址设置为如 `http://127.0.0.1:11434/v1/chat/completions`。
  * **使用 Ollama 原生格式：** 将接口地址设置为以 `/api/generate` 结尾（如 `http://127.0.0.1:11434/api/generate`）。模组会自动检测并切换到 Ollama 原生的提问格式。
* **触发器：** 开启/关闭定时触发或事件驱动触发。
* **提示词：** 自定义系统提示词 (System Prompt) 以定义女巨人的个性。

### 🚀 CI/CD & Development

This project uses GitHub Actions for automated building and releasing.

*   **Release Version:** Push a tag starting with `V` (e.g., `V1.0.0`) to trigger a full GitHub Release.
*   **Dev Version:** Push a tag named `dev` to trigger a build and upload the DLL as an **Artifact** (downloadable from the Actions page).

#### Dependency Management
All necessary libraries (UnityEngine, BepInEx, etc.) are stored in the `libs/` folder. This allows the project to be compiled in a clean environment like GitHub Actions. If you update your game and need to update dependencies, copy the new DLLs into the `libs/` folder.

---

### 🚀 持续集成与开发 (CI/CD)

项目使用 GitHub Actions 进行自动构建和发布。

*   **正式版本：** 推送以 `V` 开头的标签（例如 `V1.0.0`）将触发自动创建 GitHub Release。
*   **开发版本：** 推送名为 `dev` 的标签将触发构建，并将生成的 DLL 上传为 **Artifact**（可在 Actions 页面下载）。

#### 依赖管理
所有必要的库（UnityEngine、BepInEx 等）都存放在 `libs/` 文件夹中。这使得项目可以在 GitHub Actions 等干净的环境中编译。如果你更新了游戏并需要更新依赖，请将新的 DLL 复制到 `libs/` 文件夹中。

---


### ⚠️ Disclaimer / 免责声明

This mod is intended for use with "Giantess Sandbox". Please use it responsibly and in accordance with the game's community guidelines.
本模组仅供在 "Giantess Sandbox" 中使用。请负责任地使用，并遵守游戏社区准则。
