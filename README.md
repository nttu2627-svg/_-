# _-
# _-

This repository contains multiple Unity projects alongside the AI China Town prototype. Some of the
folders – especially the Unity `Library` and `Logs` directories – are very large, which can make it
hard to use GitHub's built-in search or other web UIs efficiently.

## Working with the repository locally

To explore or search the codebase, clone the repository and use local command-line tools that are
optimised for large projects:

1. Install [ripgrep](https://github.com/BurntSushi/ripgrep) (`rg`) and
   [fd](https://github.com/sharkdp/fd) if they are not already available on your system.
2. Use `rg` to search for text across the tracked source files:

   ```bash
   rg "search term"
   ```

   `rg` automatically respects `.gitignore`, so heavy Unity artefacts in `Library/` and other
   generated folders are skipped.
3. Use `fd` when you need to find files by name without traversing the entire directory tree:

   ```bash
   fd "pattern" src/
   ```

4. When working specifically on the AI China Town Python project, activate its virtual environment
   and install dependencies from `ai_china_town-master/requirements.txt`.

## Repository structure

- `Map/` – Main Unity project. Generated folders (`Library/`, `Logs/`, `obj/`, etc.) are kept to
  preserve the original environment but can be deleted locally to save space.
- `Map (1)/Map/` – Secondary Unity assets and tooling.
- `ai_china_town-master/` – Python project for the AI China Town simulation.

Cleaning up the generated Unity folders locally and relying on `rg`/`fd` for searches should make it
possible to navigate and understand the code without running into size-related limitations.
# Map & ai_china_town-master

結合 Unity 前端與 Python 後端的 MBTI 都市代理人模擬專案。

## 專案結構

### Map (Unity 專案)

Assets/
├── Prefabs/
│ ├── Agents/ # 16種MBTI代理人與對話泡泡
│ └── UI/ # UI元件
├── Scripts/
│ ├── Agent/ # 代理人顯示控制
│ ├── Building/ # 建築互動
│ ├── Camera/ # 攝影機控制
│ ├── UI/ # 介面控制
│ ├── World/ # 場景互動
│ ├── ThirdParty/ # 第三方套件
│ ├── Utility/ # 公用工具
│ ├── CameraController.cs
│ ├── DataModels.cs
│ ├── Panel.cs
│ ├── SceneController.cs
│ ├── SimulationClient.cs
│ ├── UIController.cs
│ ├── UIManager.cs
│ └── UnityMainThreadDispatcher.cs
├── Scenes/
│ └── CityScene.unity # 主要城市場景
├── Fonts/
├── Modern_Exteriors_16x16/
├── Pixel Art Top Down - Basic/
├── moderninteriors-win/
└── TextMesh Pro/


其他：  
- `Assembly-CSharp.csproj`, `Map.sln`: Unity 專案/解決方案

### ai_china_town-master (Python 後端)

ai_china_town-master/
├── README.md
├── requirements.txt
├── agents/ # 代理人初始設定
├── simulation_logic/ # 主要模擬核心
├── src/ # 程式進入與資料
├── tools/
│ ├── Database/ # 向量資料庫
│ └── LLM/ # LLM及提示詞
├── log/ # 執行日誌
├── readme_img/ # 說明圖檔
├── test/ # 測試腳本
└── ...



---

## 安裝與執行

### 前端（Unity）

1. 使用 Unity Hub 匯入 `Map` 資料夾
2. 安裝所需美術與字型資源
3. 開啟 `CityScene.unity` 測試

### 後端（Python）

1. 安裝 Python 3.9+
2. 安裝依賴：
pip install -r requirements.txt


3. 啟動主邏輯：
python src/main.py


需與 Unity 溝通時：
python src/unity_socket_main.py



---

## 主要特點

- 16 種 MBTI 代理人動態模擬
- 多層次目錄與腳本結構，便於維護與擴展
- 完整的日誌與測試工具
- 彈性介接 LLM 及外部資料庫

## 聯絡 & 貢獻

歡迎 issue、pull request 或聯絡專案作者協作！

---

> 詳細腳本註解與使用說明請參閱各目錄內源碼與附檔