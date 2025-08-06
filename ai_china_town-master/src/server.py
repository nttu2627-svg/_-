# server.py (已修正)

from fastapi import FastAPI
from pydantic import BaseModel
import uvicorn
import threading
import json
from datetime import datetime, timedelta

# <<< 新增導入 >>>
import time
import traceback
# <<< 新增導入 >>>

# 導入您的主模擬邏輯
# 確保 main_quake2.py 在同一個目錄或 Python 的 sys.path 中
try:
    # 假設您的主文件現在叫做 main_simulator.py
    import main_quake2 as simulation_core 
except ImportError:
    print("警告：無法導入 main_simulator.py，請確認檔案名稱與路徑。")
    # 如果找不到，提供一個假的模擬函數以避免伺服器啟動失敗
    class MockSimulation:
        def simulate_town_life(*args, **kwargs):
            yield "模擬核心檔案未找到，這是測試日誌。"
            time.sleep(2)
            yield "模擬核心檔案未找到，伺服器仍在運行。"
    simulation_core = MockSimulation()


# --- API 數據模型 ---
class SimulationConfig(BaseModel):
    total_sim_duration_minutes: int = 480
    min_per_step_normal: int = 10
    start_year: int = 2024
    start_month: int = 11
    start_day: int = 18
    start_hour: int = 3
    start_minute: int = 0
    selected_mbti_list: list[str] = ["ISTJ", "ENFP", "ESFJ"]
    eq_enabled: bool = True
    eq_events_json_str: str = json.dumps([{"time": "2024-11-18-08-00", "duration": 5, "intensity": 0.7}])

# --- 全局狀態管理 ---
class SimulationManager:
    def __init__(self):
        self.simulation_instance = None
        self.simulation_thread = None
        self.current_state = {"status": "idle", "log": [], "agents": [], "buildings": []}
        self.is_running = False
        self.lock = threading.Lock()

    def start_simulation(self, config: SimulationConfig):
        with self.lock:
            if self.is_running:
                return {"error": "Simulation is already running."}
            
            self.is_running = True
            self.current_state = {"status": "running", "log": ["模擬啟動中..."], "agents": [], "buildings": []}
            self.simulation_thread = threading.Thread(
                target=self._run_simulation_loop,
                args=(config,)
            )
            self.simulation_thread.start()
            return {"message": "Simulation started successfully."}

    def _run_simulation_loop(self, config: SimulationConfig):
        try:
            fake_eq_step_minutes = 1 
            sim_generator = simulation_core.simulate_town_life(
                config.total_sim_duration_minutes,
                config.min_per_step_normal,
                config.start_year, config.start_month, config.start_day,
                config.start_hour, config.start_minute,
                config.selected_mbti_list,
                config.eq_enabled,
                config.eq_events_json_str,
                fake_eq_step_minutes
            )

            for log_snapshot in sim_generator:
                with self.lock:
                    if not self.is_running:
                        self.current_state["log"].append("模擬被手動停止。")
                        break 
                    
                    self.current_state["log"] = log_snapshot.split('\n')
                    # 這裡可以加入更複雜的狀態解析邏輯
                    
                time.sleep(0.1) 
        
        except Exception as e:
            error_msg = f"模擬線程出錯: {e}"
            print(error_msg)
            traceback.print_exc()
            with self.lock:
                self.current_state["log"].append(error_msg)
                self.current_state["status"] = "error"

        finally:
            with self.lock:
                self.is_running = False
                if self.current_state["status"] == "running":
                    self.current_state["status"] = "finished"
                self.current_state["log"].append("模擬執行結束。")
                print("Simulation finished.")

    def stop_simulation(self):
        with self.lock:
            if not self.is_running:
                return {"message": "Simulation is not running."}
            self.is_running = False
        
        if self.simulation_thread:
            self.simulation_thread.join(timeout=5.0) # 添加超時以防死鎖
        
        return {"message": "Simulation stopped."}

    def get_current_state(self):
        with self.lock:
            return self.current_state.copy()

# --- FastAPI 應用實例 ---
app = FastAPI(title="AI Town Simulation API")
sim_manager = SimulationManager()

@app.post("/start_simulation")
async def start_sim(config: SimulationConfig):
    return sim_manager.start_simulation(config)

@app.get("/simulation_state")
async def get_sim_state():
    return sim_manager.get_current_state()

@app.post("/stop_simulation")
async def stop_sim():
    return sim_manager.stop_simulation()

@app.get("/")
async def root():
    return {"message": "AI Town Simulation Server is running. Use /docs to see the API."}

# --- 運行伺服器 ---
if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8000)