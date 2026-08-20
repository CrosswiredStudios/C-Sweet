# vLLM on Windows with an NVIDIA RTX PRO 6000 Blackwell

This guide runs Qwen3.8-27B as an OpenAI-compatible vLLM server on Windows 11 with Docker Desktop, WSL 2, and an NVIDIA RTX PRO 6000 Blackwell. It keeps the BF16 model and vLLM cache on Docker's Linux filesystem for fast startup and supports vision, reasoning, tool calling, prefix caching, and MTP speculative decoding.

## Prerequisites

- Windows 11 with WSL 2
- Docker Desktop using Linux containers and the WSL 2 backend
- A current NVIDIA driver with WSL GPU support
- An NVIDIA RTX PRO 6000 Blackwell with 96 GB VRAM
- PowerShell
- The Hugging Face `hf` CLI if the model has not been downloaded
- Sufficient Docker disk-image space for the model, image, and cache

This guide pins vLLM 0.26.0. Review release notes before changing the version.

## 1. Set local variables

Choose a temporary Windows staging directory for the model:

```powershell
$ModelSource = "X:\path\to\Qwen3.8-27B"
$VllmImage = "vllm/vllm-openai:v0.26.0"
```

Replace the placeholder path with an actual local directory. Run the remaining setup commands in the same PowerShell session.

## 2. Verify Docker and GPU access

```powershell
docker info
```

```powershell
docker pull $VllmImage
```

```powershell
docker run --rm --gpus all --entrypoint vllm $VllmImage --version
```

```powershell
docker run --rm --gpus all --entrypoint nvidia-smi $VllmImage
```

The version command should report `0.26.0). Using a pinned tag prevents Docker from silently reusing an outdated cached `latest` image.

## 3. Download the model

Skip this step if the complete model already exists in `$ModelSource`.

```powershell
New-Item -ItemType Directory -Force $ModelSource | Out-Null; hf download Qwen/Qwen3.8-27B --local-dir $ModelSource
```

Confirm that all 18 checkpoint shards are present:

```powershell
(Get-ChildItem -LiteralPath $ModelSource -Filter "model-*.safetensors").Count
```

Do not start vLLM until the checkpoint, index, tokenizer, processor, configuration, and chat-template files have finished downloading.

## 4. Copy the model into Docker's Linux filesystem

Reading a Windows bind mount from a Linux container crosses the WSL 9P boundary and can make safetensors loading extremely slow. A Docker named volume avoids that cost on every subsequent startup.

Create persistent volumes:

```powershell
docker volume create qwen38-model
```

```powershell
docker volume create qwen38-vllm-cache
```

Copy the model once:

```powershell
docker run --rm --mount "type=bind,source=$ModelSource,target=/source,readonly" --mount "type=volume,source=qwen38-model,target=/destination" --entrypoint /bin/cp $VllmImage -a /source/. /destination/
```

Verify the volume:

```powershell
docker run --rm --mount "type=volume,source=qwen38-model,target=/model,readonly" --entrypoint /bin/ls $VllmImage -lh /model
```

The Windows copy can be retained as a backup or removed separately after the Docker volume has been verified.

## 5. Start vLLM

### Recommended 131K baseline

Start with this profile before increasing context or concurrency:

```powershell
docker run --rm --name qwen38-vllm --gpus all --ipc=host -p 8000:8000 -e VLLM_NO_USAGE_STATS=1 --mount "type=volume,source=qwen38-model,target=/models/qwen38,readonly" --mount "type=volume,source=qwen38-vllm-cache,target=/root/.cache/vllm" $VllmImage /models/qwen38 --served-model-name qwen3.8-27b --tensor-parallel-size 1 --dtype bfloat16 --kv-cache-dtype bfloat16 --max-model-len 131072 --max-num-seqs 2 --max-num-batched-tokens 16384 --gpu-memory-utilization 0.92 --enable-prefix-caching --enable-chunked-prefill --reasoning-parser qwen3 --enable-auto-tool-choice --tool-call-parser qwen3_coder --spec-method mtp --spec-tokens 2 --load-format safetensors --safetensors-load-strategy lazy
```

Wait for:

```text
Application startup complete.
```

### Native 262K target

After validating the baseline, use this full-context profile:

```powershell
docker run --rm --name qwen38-vllm --gpus all --ipc=host -p 8000:8000 -e VLLM_NO_USAGE_STATS=1 --mount "type=volume,source=qwen38-model,target=/models/qwen38,readonly" --mount "type=volume,source=qwen38-vllm-cache,target=/root/.cache/vllm" $VllmImage /models/qwen38 --served-model-name qwen3.8-27b --tensor-parallel-size 1 --dtype bfloat16 --kv-cache-dtype bfloat16 --max-model-len 262144 --max-num-seqs 4 --max-num-batched-tokens 16384 --gpu-memory-utilization 0.95 --enable-prefix-caching --enable-chunked-prefill --reasoning-parser qwen3 --enable-auto-tool-choice --tool-call-parser qwen3_coder --spec-method mtp --spec-tokens 2 --load-format safetensors --safetensors-load-strategy lazy
```

`--max-num-seqs 4` permits four active requests; it does not guarantee four simultaneous 262K contexts. All requests share the available KV-cache pool.

The checkpoint contains one MTP hidden layer. vLLM can reuse it for two speculative tokens, but acceptance and speed should be compared with `--spec-tokens 1`. MTP affects decoding, not long-prompt prefill.

## 6. Test the API

Use IPv4 explicitly from the Windows host:

```powershell
curl.exe -sS http://127.0.0.1:8000/health
```

List models:

```powershell
Invoke-RestMethod -Uri "http://127.0.0.1:8000/v1/models"
```

Ask for a short joke without thinking mode:

```powershell
$body = @{ model = "qwen3.8-27b"; messages = @(@{ role = "user"; content = "Tell me a short, genuinely funny joke." }); max_tokens = 128; temperature = 0.8; stream = $false; chat_template_kwargs = @{ enable_thinking = $false } } | ConvertTo-Json -Depth 5; (Invoke-RestMethod -Uri "http://127.0.0.1:8000/v1/chat/completions" -Method Post -ContentType "application/json" -Body $body -TimeoutSec 120).choices[0].message.content
```

The first request can trigger additional kernel compilation and may be slower than later requests.

## 7. Monitor the server

GPU status:

```powershell
nvidia-smi
```

Container logs:

```powershell
docker logs -f qwen38-vllm
```

Useful vLLM metrics:

```powershell
curl.exe -s http://127.0.0.1:8000/metrics | Select-String 'vllm:(num_requests_running|num_requests_waiting|kv_cache_usage_perc|prompt_tokens_total|generation_tokens_total|spec_decode_num_draft_tokens_total|spec_decode_num_accepted_tokens_total)'
```

`VLLM_NO_USAGE_STATS=1` disables outbound anonymous telemetry. It does not disable the local `/metrics` endpoint. Removing it enables telemetry but does not expose additional local performance data.

## 8. Performance expectations

- High VRAM use at idle is normal. `--gpu-memory-utilization` reserves memory for weights, activations, CUDA graphs, and the KV-cache pool.
- Reserved VRAM is not the same as GPU compute utilization.
- Linux-native model storage improves startup, not steady-state token generation after the weights are in VRAM.
- Persistent `/root/.cache/vllm` storage reduces repeated compilation work when compatible artifacts can be reused.
- Very long prompts spend most of their time in prefill. Speculative decoding begins only during output generation.
- Lowering `--gpu-memory-utilization` frees VRAM for other applications but does not inherently increase generation speed.

## 9. Stop and restart

Stop the foreground server with `Ctrl+C), or from another PowerShell window:

```powershell
docker stop qwen38-vllm
```

Because the command uses `--rm`, the container is removed after stopping. The named model and cache volumes persist.

## References

- [Qwen3.8-27B](https://huggingface.co/Qwen/Qwen3.8-27B)
- [vLLM 0.26.0 serve arguments](https://docs.vllm.ai/en/v0.26.0/cli/serve/)
- [vLLM Docker deployment](https://docs.vllm.ai/en/v0.26.0/deployment/docker/)
- [vLLM production metrics](https://docs.vllm.ai/en/stable/usage/metrics/)
- [Docker volumes](https://docs.docker.com/engine/storage/volumes/)
- [WSL filesystem performance](https://learn.microsoft.com/en-us/windows/dev-environment/wsl-interop)
