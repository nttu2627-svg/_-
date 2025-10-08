# _-

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