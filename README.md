# RagChat

Sample code for [RAG in ASP.NET Core (.NET 10): A Chat API That Remembers](https://codewithmukesh.com/blog/rag-in-aspnet-core/).

A .NET 10 Minimal API that answers questions over a folder of Markdown articles, cites its sources, and remembers each user's conversation across restarts. Retrieval, reranking and memory run on [GoodMem](https://goodmem.ai), self-hosted in Docker.

## What you need

- .NET 10 SDK
- Docker Desktop
- A local GoodMem server (see below)
- An OpenAI API key (embeddings and the chat model)
- A Voyage AI API key (optional, only for the reranker)

## 1. Run GoodMem locally

Follow the local install on the [GoodMem quick start](https://goodmem.ai/quick-start). When it finishes you get a server on `http://localhost:8080`, a web console at `http://localhost:8080/console`, and an API key that starts with `gm_`.

On Windows the installer works against Docker Desktop without WSL, after two workarounds.

1. The one-line installer, run from Git Bash, downloads the CLI and then fails while copying it into place. Download `goodmem-windows-amd64.exe.tar.gz` from `get.goodmem.ai/tag/latest/` and extract it by hand.
2. The installer verifies the image signatures with cosign, and it fails while marking the downloaded cosign binary as executable. Download `cosign-windows-amd64.exe` (I used 3.1.3) from the sigstore/cosign releases page, rename it to `cosign.exe`, and put it on your PATH.

Then run the install:

```bash
goodmem system install --handsfree --skip-docker-install --tls-disabled --profile-name ragchat --db-password "your-password-min-14-chars"
```

There is also a `--skip-verify` flag that gets past the cosign problem, but it turns off the signature check, so I would not use it.

## 2. Set the keys

Keys live in user secrets, never in `appsettings.json`.

```bash
dotnet user-secrets set "GoodMem:ApiKey" "<your GoodMem key>" --project RagChat.Api
dotnet user-secrets set "GoodMem:OpenAiApiKey" "<your OpenAI key>" --project RagChat.Api
dotnet user-secrets set "GoodMem:VoyageApiKey" "<your Voyage key>" --project RagChat.Api
```

Leave the Voyage key out if you do not want the reranker. Everything else still works.

## 3. Run the API

```bash
dotnet run --project RagChat.Api --launch-profile http
```

The API listens on `http://localhost:5162`. The Scalar API reference is at `http://localhost:5162/scalar`.

## 4. Set up GoodMem and ingest the documents

```bash
curl -X POST http://localhost:5162/api/setup
```

This registers the embedder, the LLM and the reranker, creates the space, and ingests every Markdown file in `RagChat.Api/Documents`. It is safe to run again: every resource has a fixed ID, so a second run skips what already exists, after checking that each existing document finished processing.

The setup only creates what is missing. If you edit a Markdown file, or change a model name or a provider key, a second run will not pick up the change. Delete that memory or update that resource in GoodMem first.

## 5. Ask a question

Two demo users are defined in `appsettings.Development.json`. The API key decides who you are.

```bash
curl -X POST http://localhost:5162/api/chat \
  -H "X-Api-Key: alice-demo-key" \
  -H "Content-Type: application/json" \
  -d '{ "question": "My project is called Zephyr Orders. Should I use HybridCache or IMemoryCache?", "useReranker": false }'
```

Set `useReranker` to `true` to rerank the results before the answer is written.

## 6. The restart test

1. Ask a question as `alice` that tells the API something about you, like the one above.
2. Stop the API.
3. Restart GoodMem: `docker restart goodmem-<profile>-db goodmem-<profile>-server`
4. Start the API again.
5. Ask `"What is my project called?"` as `alice`. It still knows.
6. Ask the same thing as `bob` (`X-Api-Key: bob-demo-key`). It has no record, because memory is bound to the authenticated user.

To delete everything a user has said:

```bash
curl -X DELETE http://localhost:5162/api/memory -H "X-Api-Key: alice-demo-key"
```

## 7. Measure the reranker

`RagChat.Api/Eval/golden-set.json` holds a set of questions, each with the article that should answer it.

```bash
curl -X POST "http://localhost:5162/api/eval?runs=3"
```

The response reports hit@1, hit@3 and MRR, plus p50 and p95 latency, for plain retrieval and for retrieval with the reranker. Add `&withAnswer=true` to time the full pipeline including the LLM.

A Voyage account without a payment method is rate limited, and the eval will fail with `RERANKING_FAILED` (HTTP 429 from Voyage) after the first few calls. Adding a payment method lifts the limit, and the free token allowance still applies.

## Using GoodMem Cloud instead of a local install

The API reads the server address from `GoodMem:BaseUrl`. It defaults to `http://localhost:8080` in `appsettings.json`. To run against a GoodMem Cloud instance, set the base URL and the key of that instance, and leave everything else as it is.

```bash
dotnet user-secrets set "GoodMem:BaseUrl" "https://<your-instance>.app.goodmem.ai" --project RagChat.Api
dotnet user-secrets set "GoodMem:ApiKey" "<your Cloud API key>" --project RagChat.Api
```

To go back to the local server, remove the override with `dotnet user-secrets remove "GoodMem:BaseUrl" --project RagChat.Api` and set the local key again.

## Endpoints

| Endpoint | Auth | What it does |
|---|---|---|
| `POST /api/setup` | none | Registers providers, creates the space, ingests `Documents` |
| `POST /api/chat` | `X-Api-Key` | Answers a question with citations and remembers the turn |
| `DELETE /api/memory` | `X-Api-Key` | Deletes the caller's conversation memory |
| `POST /api/eval` | none | Runs the golden set with and without the reranker |

`/api/setup` and `/api/eval` are open because this is a local demo. Put them behind an admin policy before you deploy anything like this.

Happy Coding :)
