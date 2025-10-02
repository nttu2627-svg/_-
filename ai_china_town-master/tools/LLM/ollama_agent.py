# tools/LLM/ollama_agent.py (錯誤日誌強化版)

import json
import sys
import aiohttp
from datetime import datetime # <--- 步驟一：導入 datetime 模組

class OllamaAgent:
    def __init__(self, model="deepseek-r1:14b", api_url="http://127.0.0.1:11434/api"):
        self.model = model
        self.baseurl = api_url
        connector = aiohttp.TCPConnector(limit=1024 * 1024)
        self.session = aiohttp.ClientSession(connector=connector)

    async def close_session(self):
        if self.session and not self.session.closed:
            await self.session.close()

    async def ollama_stream_request(self, prompt: str):
        endpoint = f"{self.baseurl}/generate"
        data = {"model": self.model, "prompt": prompt, "stream": True}
        try:
            async with self.session.post(endpoint, json=data, timeout=900) as response:
                response.raise_for_status()
                async for line in response.content:
                    if line:
                        try:
                            yield json.loads(line.decode('utf-8'))
                        except json.JSONDecodeError:
                            # 在流式數據中，有時會收到非json的控制字符，忽略它們
                            continue
        except Exception as e:
            # --- 核心修改在此 ---
            # 步驟二：獲取當前時間並格式化
            current_time = datetime.now().strftime('%Y-%m-%d %H:%M:%S')
            
            # 步驟三：輸出包含時間、錯誤類型和錯誤內容的詳細日誌
            # 使用多行字串和換行符(\n)讓日誌在終端機中更易於閱讀
            print(
                f"❌ [OLLAMA_STREAM_ERROR] @ {current_time} - 請求 Ollama API 時出錯。\n"
                f"   錯誤類型: {type(e).__name__}\n"
                f"   詳細內容: {e}",
                file=sys.stderr
            )
            # --- 修改結束 ---


    # --- 後續的 ollama_stream_generate_response 和 generate_prompt 函式保持不變 ---
    async def ollama_stream_generate_response(self, prompt: str, special_instruction="", expect_json=True, example_output=None):
        full_response_content = ""
        
        if expect_json:
            wrapped_prompt = (
                '"""\n' + prompt.strip() + '\n"""\n'
                f"Output the response to the prompt above in json. {special_instruction}\n"
                "Example output json\n```json\n"
                + json.dumps({"output": example_output}, ensure_ascii=False)
                + "\n```"
            )
        else:
            wrapped_prompt = f"{prompt.strip()}\n{special_instruction}"

        try:
            async for chunk in self.ollama_stream_request(wrapped_prompt):
                if chunk and "response" in chunk:
                    full_response_content += chunk["response"]
                if chunk and chunk.get("done", False):
                    break
        except Exception as e:
            current_time = datetime.now().strftime('%Y-%m-%d %H:%M:%S')
            print(
                f"❌ [OLLAMA_STREAM_GENERATE] @ {current_time} - 在拼接流式響應時出錯。\n"
                f"   錯誤類型: {type(e).__name__}\n"
                f"   詳細內容: {e}",
                file=sys.stderr
            )
            return None
        
        return full_response_content

    @staticmethod
    def generate_prompt(curr_input, prompt_lib_file):
        if isinstance(curr_input, str): curr_input = [curr_input]
        curr_input = [str(i) for i in curr_input]
        with open(prompt_lib_file, "r", encoding="utf-8") as f:
            prompt = f.read()
        for idx, val in enumerate(curr_input):
            prompt = prompt.replace(f"!<INPUT {idx}>!", val)
        marker = "<commentblockmarker>###</commentblockmarker>"
        if marker in prompt:
            _, _, after = prompt.partition(marker)
            prompt = after or prompt.partition(marker)[0]
        return prompt.strip()