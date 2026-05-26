# GiantessLLMMod

## 中文

Hey 大家，我最近写了一个 Giantess Sandbox 的 BepInEx mod，叫 `GiantessLLMMod`。

简单来说，它是用来把游戏里的 Giantess 接到大模型上的。mod 会读取当前游戏状态，比如玩家在哪、是不是被抓住、是不是在嘴里或胃里、Giantess 当前状态、她对玩家的记忆和关系之类的信息，然后把这些内容发给 LLM。模型回复之后，mod 会把回复转换成游戏里的动作和台词。

比如你可以在游戏里对她说“过来过来”，模型可能会返回：

```json
{
  "action": "walk_to_player",
  "emotion": "gentle",
  "dialogue": "我来了，小宝贝。别跑太远，我会担心你的~",
  "ask": null
}
```

然后 mod 会让 Giantess 说话、改变表情，并尝试执行对应动作。

### 它能做什么

- 读取玩家和 Giantess 的实时状态。
- 把状态和玩家输入发给 LLM。
- 让 LLM 决定 Giantess 接下来要做什么。
- 支持说话、转向玩家、走向玩家、表情变化、拍肚子、打嗝等动作。
- 使用游戏原生 `Say()` 显示台词。
- 通过 `ollama.py` 代理连接本地或云端 OpenAI-compatible API。

### 怎么用

先确认 BepInEx 已经装好，并且 dll 在这里：

```text
BepInEx/plugins/GiantessLLMMod.dll
```

然后启动代理：

```powershell
cd D:\Games\GaintessSandBox\Workdir-BepInEx\GiantessLLMMod
python ollama.py
```

看到这行就说明代理开好了：

```text
LLM proxy started: http://127.0.0.1:11434
```

接着进游戏：

- `F8` 打开 mod 面板
- `F7` 手动触发一次 LLM 请求
- 也可以等自动触发

### 默认配置

配置文件在：

```text
BepInEx/config/com.giantess.llmmod.cfg
```

默认关键项：

```text
ApiBaseUrl = http://127.0.0.1:11434/v1
Model = miu_ai_model
DryRunMode = false
ToggleUI = F8
ManualTrigger = F7
```

### 如果没反应

先看 `ollama.py` 有没有开着。如果代理窗口没有出现 `/v1/chat/completions` 请求，那一般是 `ApiBaseUrl` 没配对。

如果她会说话，但动作没有执行，就看：

```text
BepInEx/LogOutput.log
```

里面搜 `Giantess LLM Mod`。

另外，更新 dll 的时候一定要先完全退出游戏，不然 Windows 会锁住 `GiantessLLMMod.dll`，导致覆盖失败。

---

## English

Hey everyone, I recently made a BepInEx mod for Giantess Sandbox called `GiantessLLMMod`.

In short, this mod connects the in-game Giantess AI to an LLM. It reads live game state, such as where the player is, whether the player is being held, whether they are in the mouth or stomach, the giantess state, and memory or relationship data. Then it sends that context to an LLM. The model replies with JSON, and the mod turns that JSON into in-game dialogue, expressions, and actions.

For example, if the player says "come here", the model might return:

```json
{
  "action": "walk_to_player",
  "emotion": "gentle",
  "dialogue": "I'm coming, little one. Don't run too far~",
  "ask": null
}
```

The mod will then try to make the giantess speak, change expression, and perform the requested action.

### What It Does

- Reads live player and giantess state.
- Sends game context and player input to an LLM.
- Lets the LLM decide the giantess' next response.
- Supports dialogue, facing the player, walking toward the player, expression changes, stomach pats, burps, and more.
- Uses the game's native `Say()` system for dialogue.
- Uses `ollama.py` as a local proxy for local or cloud OpenAI-compatible APIs.

### How To Use

Make sure BepInEx is installed and the dll is here:

```text
BepInEx/plugins/GiantessLLMMod.dll
```

Start the proxy:

```powershell
cd D:\Games\GaintessSandBox\Workdir-BepInEx\GiantessLLMMod
python ollama.py
```

The proxy is ready when you see:

```text
LLM proxy started: http://127.0.0.1:11434
```

Then start the game:

- `F8` opens the mod overlay
- `F7` manually triggers an LLM request
- Automatic triggers can also run in the background

### Default Config

Config file:

```text
BepInEx/config/com.giantess.llmmod.cfg
```

Important defaults:

```text
ApiBaseUrl = http://127.0.0.1:11434/v1
Model = miu_ai_model
DryRunMode = false
ToggleUI = F8
ManualTrigger = F7
```

### If It Does Nothing

First check that `ollama.py` is running. If the proxy window does not show `/v1/chat/completions`, the `ApiBaseUrl` is probably wrong.

If dialogue works but actions do not, check:

```text
BepInEx/LogOutput.log
```

Search for `Giantess LLM Mod`.

Also, fully close the game before replacing the dll. Otherwise Windows will keep `GiantessLLMMod.dll` locked.
