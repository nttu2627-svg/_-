# tools/LLM/ollama_agent.py (配套修正版)

import json
import sys
import aiohttp

class OllamaAgent:
    def __init__(self, model="deepseek-r1:14b", api_url="http://127.0.0.1:11434/api"):
        self.model = model
        self.baseurl = api_url
        self.session = aiohttp.ClientSession()

    async def close_session(self):
        if self.session and not self.session.closed:
            await self.session.close()

    async def ollama_stream_request(self, prompt: str):
        endpoint = f"{self.baseurl}/generate"
        data = {"model": self.model, "prompt": prompt, "stream": True}
        try:
            async with self.session.post(endpoint, json=data, timeout=300) as response:
                response.raise_for_status()
                async for line in response.content:
                    if line:
                        try:
                            yield json.loads(line.decode('utf-8'))
                        except json.JSONDecodeError:
                            continue
        except Exception as e:
            print(f"❌ [OLLAMA_STREAM_ERROR] 请求 Ollama API 时出错: {e}", file=sys.stderr)

    async def ollama_stream_generate_response(self, prompt: str, special_instruction="", expect_json=True, example_output=None):
        full_response_content = ""
        
        # ### 核心修改：根据 expect_json 决定是否包装 prompt ###
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
                if "response" in chunk:
                    full_response_content += chunk["response"]
                if chunk.get("done", False):
                    break
        except Exception as e:
            print(f"❌ [OLLAMA_STREAM_GENERATE] 在拼接流式响应时出错: {e}", file=sys.stderr)
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