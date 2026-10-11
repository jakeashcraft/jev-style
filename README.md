# jev-style

Benchmark and C# client for decision models. It runs the workload of
[jev-expression-benchmark](https://github.com/jakeashcraft/jev-expression-benchmark) (975 Boolean expressions from a
production CIP expression engine, checked against one simulated PLC snapshot) through two kinds of model:

- **Local, through LM Studio** (`--provider logprobs`, the default):
  [chaoliangUNSW/Jev-Style-Qwen3.5-2B-Decision-GGUF](https://huggingface.co/chaoliangUNSW/Jev-Style-Qwen3.5-2B-Decision-GGUF).
  It never writes text. It reads a prompt that ends with `Answer:`, and the probability of the next token being
  `A` (yes) or `B` (no) is the decision.
- **Hosted, through OpenRouter's decisions API** (`--provider decisions`): TypeSafe's Jev 1.13, Microsoft-Decision-1
  and the other decision models OpenRouter lists.

Everything that decides the answer is ported from `bench.mts` and checked against it: the same rows, ids, tag
values, rules text, request payloads, batching, warm-up, retries, timing and scoring, and the same results-file
shape. A run here and a run of `bench.mts` against the TypeSafe API compare one to one.

## Results (2026-10-11)

Dataset: `data/` from jev-expression-benchmark at commit 1632350 (975 expressions, 309 tags, 378 true and 597
false). The client ran on the Windows machine that runs LM Studio (RTX 4090), so hosted calls include the internet
round trip. The TypeSafe API rows are your own `bench.mts` runs from 2026-09-22 on the same published dataset.

| Model | Route | Expressions per request x in flight | Accuracy | Wall time per pass | Expressions/s | p50 per request | Cost per pass |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| Jev-style 2B v1 (Q8_0) | LM Studio | 1 x 1 | 79.28% | 77.6 s | 13 | 83 ms | free |
| Jev-style 2B v1 (Q8_0) | LM Studio | 1 x 16 | 78.77% | 65.2 s | 15 | 1,057 ms | free |
| Microsoft-Decision-1 | OpenRouter | 1 x 16 | 99.49% | 21.2 s | 46 | 271 ms | $0.0054 |
| Microsoft-Decision-1 | OpenRouter | 1 x 128 | 99.28% | 4.5 s | 217 | 307 ms | $0.0054 |
| Microsoft-Decision-1 | OpenRouter | 50 x 20 (3 runs) | 98.87 to 99.08% | 1.8 to 2.2 s | 452 to 534 | 601 to 663 ms | $0.0042 |
| Jev 1.13 | OpenRouter | 1 x 16 | 99.9% | 16.8 s | 58 | 191 ms | $0.0156 |
| Jev 1.13 | OpenRouter | 1 x 128 | 99.9% | 4.0 s | 247 | 192 ms | $0.0156 |
| Jev 1.13 | OpenRouter | 50 x 20 (3 runs) | 99.28 to 99.49% | 1.4 to 2.0 s | 498 to 687 | 209 to 252 ms | $0.0041 |
| Jev 1.13 | TypeSafe API | 1 x 128 (3 runs) | 99.9% | 1.4 to 2.9 s | 342 to 694 | 149 to 181 ms | $0.0156 |
| Jev 1.13 | TypeSafe API | 50 x 20 (5 runs) | 99.18 to 99.38% | 0.25 to 0.34 s | 2,901 to 3,841 | 141 to 213 ms | $0.0041 |

Accuracy by expression shape, one expression per request (best run of each):

| Shape | Rows | Jev-style 2B | Microsoft-Decision-1 | Jev 1.13, OpenRouter | Jev 1.13, TypeSafe API |
| --- | ---: | ---: | ---: | ---: | ---: |
| One tag compared with true or false | 67 | 94.03% | 100% | 100% | 100% |
| One tag compared with a number | 168 | 66.07% | 100% | 100% | 100% |
| One tag, compound (ranges, OR lists) | 99 | 80.81% | 98.99% | 100% | 100% |
| Two or more tags | 641 | 80.97% | 99.38% | 99.84% | 99.84% |

What the numbers say:

- **Jev answers the same through OpenRouter as through the TypeSafe API.** One expression per request it scores
  99.9% both ways and misses the same row (r924, one of the 40 rows that can never be true), batched it scores 99.2 to
  99.5% both ways, and the token counts are identical. OpenRouter adds time in the tail rather than the middle: p50
  is about the same (191 against 149 to 181 ms) but p95 reaches 0.9 to 1.5 s, so a pass with 128 in flight takes
  4 s instead of 1.4 to 2.9 s, and a batched pass 1.4 to 2 s instead of 0.25 to 0.34 s.
- **Microsoft-Decision-1 is close but a step behind on this workload:** 99.3 to 99.5% one per request and 98.9 to
  99.1% batched. It misses five to seven rows that Jev gets right, mostly ones where loose equality matters
  (`CIP_37_HTST_Step == true` is `CIP_37_HTST_Step == 1`) or rows that can't change value. It is the cheapest way
  to send one expression per request ($0.0054 a pass against $0.0156) because its requests are shorter; batched,
  the two cost the same.
- **The local 2B model is not close: 78 to 79%.** It does well on a single Boolean comparison (94%) and worst on a
  single numeric comparison (66%). It leans toward "no" (true recall 63%, false recall 89%). Its probabilities are
  honest (calibration error 0.05): answers at 0.9 confidence or higher are 99.4% right, but that is only 17% of rows.
- **LM Studio worked through about one request at a time.** Sixteen in flight raised throughput from 13 to 15
  expressions/s while each request waited about 1 s instead of 83 ms, and batching doesn't apply because the model
  answers one question per prompt. A pass takes 65 to 78 s locally against 4 s for Jev through OpenRouter.
- **Answers move a little between runs.** Seventeen rows flipped between the three local runs (mean change in
  probability 0.005), and Microsoft-Decision-1 flipped two rows between its two per-request runs. Jev's
  per-request answers matched across all five runs.

The full tables (by shape, rows that can't change value, how often runs agree, and every missed row) are in
`results/2026-10-11-comparison-one-per-request.md` and `results/2026-10-11-comparison-batched.md`, written by the
`compare` command. `results/` is not committed; runs from the earlier port of jev-benchmark are in
`results/archive-jev-benchmark/`.

## What's in the repo

| Path | What it is |
| --- | --- |
| `src/JevStyle.Decisions` | Reusable client library (NuGet-ready). Builds the model-card prompt, reads `top_logprobs` from any OpenAI-compatible server and returns Choice, Bool and Score decisions with calibrated probabilities. Also a client for OpenRouter's decisions API and an adapter that puts it behind the same `IDecisionClient` interface. |
| `src/JevStyle.Benchmark` | The `jev-style-bench` console: a C# port of `bench.mts` (dataset loading, evaluator, snapshot generator, sweep runner, results file) with an LM Studio transport, an OpenRouter transport and a `compare` command. |
| `data` | A copy of jev-expression-benchmark's `data` folder (commit 1632350): `expressions.csv` and `tags.json`. `--data` points anywhere else. |
| `tests/JevStyle.Decisions.Tests` | Prompt format, log-probability decoding, HTTP contract and DI tests for both transports. |
| `tests/JevStyle.Benchmark.Tests` | Parity tests against `bench.mts` itself, evaluator and sweep runner tests, and end-to-end CLI tests against a fake LM Studio and a fake OpenRouter. |

## Requirements

- .NET 10 SDK (`global.json` picks the newest 10.0.x).
- For the local model: LM Studio with the server running (Developer tab, or `lms server start`) and the model
  loaded with a small context. 4,096 tokens is plenty: the longest prompt here is about 500 tokens. See
  [Troubleshooting](#troubleshooting) for why the default 262,144 is a problem.
- For hosted models: an OpenRouter API key in `OPENROUTER_API_KEY`. The benchmark also reads the Windows user
  and machine environment, so a terminal that was already open picks up a newly set key. The key goes only into
  the `Authorization` header and never into the results.

```powershell
lms load jev-style-qwen3.5-2b-decision --context-length 4096 -y
[Environment]::SetEnvironmentVariable('OPENROUTER_API_KEY', '<your key>', 'User')
```

## Run it

From PowerShell in the repo root:

```powershell
dotnet build -c Release

# The local model, one expression per request, at 1, 4 and 16 in flight.
dotnet run --project src/JevStyle.Benchmark -c Release -- --url http://localhost:1234 --concurrency 1,4,16

# Hosted models: one expression per request with 16 in flight, then 50 per request three times, all in flight.
dotnet run --project src/JevStyle.Benchmark -c Release -- --provider decisions --model typesafe/jev-1.13 --batch 1 --concurrency 16
dotnet run --project src/JevStyle.Benchmark -c Release -- --provider decisions --model microsoft/microsoft-decision-1 --batch 50,50,50 --concurrency 128

# Compare any runs, from this tool or bench.mts, on the rows they all answered. #n picks one run of a file.
dotnet run --project src/JevStyle.Benchmark -c Release -- compare `
  results\2026-10-11-local-jev-style-2b.json#1 `
  results\2026-10-11-jev-1.13-batch1.json `
  ..\..\typesafe-ai\results\2026-09-22T08-02-40-636Z.json --output results\comparison.md

# Quick checks.
dotnet run --project src/JevStyle.Benchmark -c Release -- --limit 20
dotnet run --project src/JevStyle.Benchmark -c Release -- --provider decisions --batch 50 --dry-run
dotnet run --project src/JevStyle.Benchmark -c Release -- --help
```

Set `JEVSTYLE_DATA`, `JEVSTYLE_URL`, `JEVSTYLE_MODEL` and `JEVSTYLE_API_KEY` to avoid repeating `--data`, `--url`,
`--model` and `--api-key` for the local model. `--provider decisions` defaults to OpenRouter and
`microsoft/microsoft-decision-1` and reads `OPENROUTER_API_KEY`.

| Option | Default | Meaning |
| --- | --- | --- |
| `--data <path>` | `JEVSTYLE_DATA`, else `data` | Folder with `expressions.csv` and `tags.json`. A missing `tags.json` is generated and written there, as `bench.mts` does. |
| `--provider <logprobs\|decisions>` | `logprobs` | `logprobs`: next-token log-probabilities from LM Studio or another OpenAI-compatible server. `decisions`: a hosted decisions API (OpenRouter). |
| `--url <url>` | `http://localhost:1234`, or OpenRouter for `decisions` | Server root. A trailing `/v1` is fine. For `decisions` it is the API root, `https://openrouter.ai/api/alpha/`. |
| `--model <id>` | `jev-style-qwen3.5-2b-decision`, or `microsoft/microsoft-decision-1` for `decisions` | Checked before the run: against `/v1/models` on LM Studio, against OpenRouter's list of decision models for `decisions`. |
| `--api-key <key>` | `JEVSTYLE_API_KEY`, or `OPENROUTER_API_KEY` for `decisions` | Bearer token. Prefer the environment variable, since a key on the command line ends up in shell history. |
| `--batch <n[,n...]>` | `1` | Expressions per request. A comma list sweeps; repeat a value to repeat a config. The local model only takes 1. |
| `--concurrency <n[,n...]>` | `16` | Requests in flight. A comma list sweeps. Capped at the number of requests. |
| `--limit <n>` | all rows | Only the first n rows, for cheap smoke runs. |
| `--retries <n>` | `2` | Extra attempts per request for connection failures, timeouts, 408, 429 and 5xx. `0` shows the raw failure rate. |
| `--timeout-ms <n>` | `60000` | Deadline per attempt. |
| `--option-order <true-first\|false-first>` | `true-first` | Local model only. Which option gets letter A. Run both to measure position bias. |
| `--top-logprobs <n>` | `20` | Local model only. Candidates per position. LM Studio rejects more than 20. |
| `--skip-model-check` | off | Skip the model check (for servers that don't list their models). |
| `--dry-run` | off | Load the data, build the requests and print the first one; make no model calls. |
| `--output <path>` | `results/<timestamp>-<model>.json` | Results file. |

Exit codes: 0 when every request got an answer, 1 for failed requests, skipped configs or bad input, 130 after
Ctrl+C. Ctrl+C stops new requests, cancels the ones in flight and still writes the results so far. A second Ctrl+C
ends the process.

## How it works

The steps follow `bench.mts`:

1. **Load.** Read `expressions.csv` one line per row (name before the first comma, no quoting) and keep rows whose
   characters all belong to the grammar: tag names, `true`/`false`, numbers, `== != > >= < <=`, `&&`, `||`, `!`
   and parentheses. Row ids are `r` plus the line number.
2. **Simulate the PLC.** `tags.json` holds one value per tag. If it is missing it is generated the same way:
   Boolean tags get `true`/`false`, numeric tags each compared constant and its neighbours, and of 5,000 draws from
   mulberry32 seeded with 42, the one closest to an even true/false split wins.
3. **Ground truth.** `bench.mts` compiles each row as JavaScript. The C# evaluator follows the same rules: loose
   equality (`true == 1`), relational operators before equality, `&&` before `||`, and `&&`/`||` return an operand.
4. **Build requests, untimed.** Rows are chunked in CSV order. Each request carries only the tags its rows use as
   the state, and one question per row: the rules, then the expression.
5. **Warm up, then time.** One throwaway request per worker opens the connections, then a worker pool sends the
   real requests while the clock runs. Retryable errors are retried after 50 to 150 ms, then 100 to 300 ms (or the
   server's `Retry-After`), and that time stays on the clock.
6. **Score.** Every answer is P(true); 0.5 or more counts as true.

### Local model

One request per row to `/v1/chat/completions` with `max_tokens: 1`, `temperature: 0`, `logprobs: true` and
`top_logprobs: 20`. The released GGUF ships a pass-through chat template, so the prompt reaches the model verbatim:

```text
You are a decision function. Read the state, then answer the question by choosing exactly one option.

[State]
{"CIP_01_Caustic_Supply_Valve":true,"CIP_01_Caustic_Return_Valve":true,"CIP_01_Return_Temp":110}

[Question]
Evaluate the Boolean expression against the tag values in the state; tag names are state keys. && is AND, || is OR, and && binds tighter than ||. true equals 1 and false equals 0.
Expression: CIP_01_Caustic_Supply_Valve == true && CIP_01_Caustic_Return_Valve == true && CIP_01_Return_Temp > 150
Does the expression evaluate to true?

[Options]
A. yes
B. no

Answer:
```

The state and the rules are exactly what `bench.mts` sends. The candidates for ` A` and ` B` are renormalised over
the two letters, like the model card's reference client: tokens match after trimming whitespace, the best match per
letter wins, and a letter missing from the top 20 gets the lowest candidate minus 5 nats. The results also record
**option mass**, how much of the next-token probability landed on `A` or `B` before renormalising (88% on average
here, never below 71%), which shows the model followed the format.

### Hosted models

`bench.mts`'s payload, unchanged, sent to `POST https://openrouter.ai/api/alpha/decisions` (the same shape as the
TypeSafe API's `/v1/systemone`). With `--batch 2`:

```json
{
  "model": "typesafe/jev-1.13",
  "state": { "CIP_01_Caustic_Supply_Valve": true, "CIP_01_Caustic_Return_Valve": true, "CIP_01_Return_Temp": 110, "CIP_01_Step_No": 29 },
  "questions": {
    "r2": { "type": "noul", "instructions": "<rules>\nExpression: CIP_01_Caustic_Supply_Valve == true && CIP_01_Caustic_Return_Valve == true && CIP_01_Return_Temp > 150" },
    "r3": { "type": "noul", "instructions": "<rules>\nExpression: CIP_01_Caustic_Return_Valve == true && CIP_01_Caustic_Supply_Valve == false && CIP_01_Step_No > 30" }
  }
}
```

OpenRouter also returns the dated snapshot that answered (`typesafe/jev-1.13-20260917`,
`microsoft/microsoft-decision-1-20261009`), the provider behind it (TypeSafe, Azure), token usage and cost. The
results keep all four.

## Results files

Each run writes one JSON file in `bench.mts`'s shape, with the same keys and rounding: `model`, `rules`,
`expressions`, `expectedTrue`, `localMs`, and per config `summary` (`batch`, `conc`, `reqs`, `wall s`, `expr/s`,
`p50 ms`, `p95 ms`, `max ms`, `retries`, `failed`, `acc %`, `acc true %`, `acc false %`, `tokens`, `cost $`),
`failures`, `requests`, `mismatches` and `probabilities`. Percentiles use `bench.mts`'s index
(`sorted[floor(p * n)]`), not nearest rank. Additions:

- The provider, endpoint, request format and the exact first request (no credentials); LM Studio's model details;
  the dataset folder and SHA-256 of both files.
- Per config: Brier score, output tokens, served models and providers, and the local model's option mass.
- Per request: output tokens, reported cost, served model, provider, response id, HTTP status on failure.
- `cost $` is the cost the server reported. `bench.mts` computes it at Jev's price instead; the two agree for Jev.

### Comparing runs

`jev-style-bench compare <results.json>[#n] ... [--data path] [--output file.md]` reads runs from either tool and
compares them on the rows every run answered. Expected answers come from the dataset, so a `bench.mts` file only
needs its probabilities. It reports accuracy, recalls, Brier score, time and cost per run; accuracy by expression
shape; accuracy on rows that can never change value; how often each pair of runs agrees; and every row any run
missed. It warns when a file was made with different rules text, as `bench.mts` runs from before the dataset was
published were.

## Tests

```powershell
dotnet test
```

The parity tests check the port against `bench.mts` itself. `tests/JevStyle.Benchmark.Tests/Fixtures/generate-reference.mjs`
copies the part of `bench.mts` that loads the CSV, compiles rows, collects literals and builds the snapshot (everything
before `const RULES =`, minus the SDK import), runs it on the published dataset and on 53 edge-case lines, and records
the result in `reference.json`:

```powershell
node tests\JevStyle.Benchmark.Tests\Fixtures\generate-reference.mjs ..\..\typesafe-ai
```

The C# side has to match every row id, name, tag list and expected answer, every skipped line, every tag's literals
and candidate values, the shipped and the regenerated snapshot (values and order), the answer of every row for 320
random states, and the state of the first requests at batch 1 and 50. The edge cases cover operator precedence,
loose equality, `||` and `&&` returning operands, subtraction (`A -1`), literal case, tabs, no-break spaces, line
separators, rows without a comma, and rows `bench.mts` skips. Numbers JavaScript would read as octal, exponent or
hex literals (`010`, `1e5`, `0x1F`) are rejected instead; none are in the dataset.

## Using the client library

```csharp
using JevStyle.Decisions;

var source = new OpenAiChatLogprobSource(new HttpClient(), new JevStyleClientOptions
{
    BaseAddress = new Uri("http://localhost:1234"),
    Model = "jev-style-qwen3.5-2b-decision",
});
IDecisionClient client = new DecisionClient(source);

Decision section = await client.DecideAsync(
    "Shares of the chipmaker jumped 8% after it raised its revenue forecast.",
    "Which news section does this article belong to?",
    ["World", "Sports", "Business", "Science/Technology"]);
// section.Selected.Option == "Business", section.Selected.Probability ~ 0.69

BoolDecision entails = await client.DecideBoolAsync(
    "Premise: A man is playing a guitar on stage.\nHypothesis: Someone is performing music.",
    "Does the premise entail the hypothesis?");
// entails.Probability is the probability of "yes"

ScoreDecision rating = await client.DecideScoreAsync(
    "The plot is thin, but the two leads are so charming that I left the cinema smiling.",
    "Rate the sentiment of this review on an ordered scale.",
    ["very negative", "negative", "neutral", "positive", "very positive"]);
// rating.ExpectedLevel is between 0 and 4
```

With dependency injection (`AddJevStyleDecisions` returns the `IHttpClientBuilder`, so resilience handlers,
timeouts and logging stay your choice):

```csharp
builder.Services.AddJevStyleDecisions(o =>
{
    o.BaseAddress = new Uri(builder.Configuration["JevStyle:Url"]!);
    o.Model = "jev-style-qwen3.5-2b-decision";
});
```

`ILogprobSource` is the seam for other transports (llama.cpp's native `/completion`, an in-process runtime, a
test double). `LogprobDecoder` is public if you already have the log-probabilities.

### Hosted decision models

`DecisionsApiClient` calls OpenRouter's decisions API. One call asks any number of typed questions (noul, choice,
score) about one state, which can be a JSON object of named values or plain text:

```csharp
var api = new DecisionsApiClient(new HttpClient(), new DecisionsApiOptions
{
    Model = "typesafe/jev-1.13", // default: microsoft/microsoft-decision-1
    ApiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"),
});

DecisionsApiResponse response = await api.DecideAsync(new DecisionsApiRequest(
    new JsonObject { ["Supply_Pump"] = true, ["Return_Temp"] = 151 },
    new Dictionary<string, DecisionQuestion>
    {
        ["circulating"] = new NoulQuestion("Expression: Supply_Pump == true && Return_Temp > 150"),
        ["hot"] = new NoulQuestion("Is the return temperature above 150?", WhenTrue: "It is above 150.", WhenFalse: "It is 150 or below."),
    }));

double probability = response.Answer<NoulAnswer>("circulating").Probability; // probability of true
decimal? cost = response.Usage?.Cost;                                        // USD, as reported by OpenRouter
```

A 429 or 503 throws `DecisionServerException` with the server's `RetryAfter`, so callers can wait as long as the
server asks. `HostedDecisionClient` puts any `IDecisionsApi` behind `IDecisionClient`, so code written for the local
model can switch to a hosted one. With dependency injection:

```csharp
builder.Services.AddDecisionsApi(o =>
{
    o.Model = "microsoft/microsoft-decision-1";
    o.ApiKey = builder.Configuration["OpenRouter:ApiKey"];
});
// Inject IDecisionsApi for the full API, or HostedDecisionClient where you would use IDecisionClient.
```

## Troubleshooting

**Requests suddenly take 2 to 8 seconds.** Seen on 2026-10-11 with the load settings in use at the time
(262,144-token context, 4 parallel slots, unified KV cache). After a few hundred requests in a row, about half
of all calls stalled for 3 to 6 seconds even though LM Studio's own time to first token stayed near 50 ms,
`/v1/models` stalled too, LM Studio's `main.log` showed `Engine protocol predict request failed: fetch failed`,
and the `llama-server.exe` process had grown to 17 GB of private memory for a 2 GB model. Restarting the server
alone (`lms server stop`, `lms server start`) helped for about 550 requests. Reloading with
`--context-length 4096` fixed it. Avoid killing a client in the middle of a request; Ctrl+C in this tool cancels
requests cleanly.

**More requests in flight don't help the local model.** With the current load LM Studio works through one request
at a time, so extra concurrency only adds queueing. Throughput stays near 13 to 15 expressions per second.

**The report says a different model answered.** LM Studio answers a request for an unknown model id with
whatever model is loaded. The benchmark checks `/v1/models` before it starts and records the model each
response names.

**`'top_logprobs' must be an integer between 0 and 20`.** LM Studio's limit. Keep `--top-logprobs` at 20 or less.

**No `top_logprobs` in the response.** LM Studio only returns them on `/v1/chat/completions`, not on
`/v1/completions` or `/api/v0`. That's why the client uses the chat endpoint.

**`OPENROUTER_API_KEY is missing`.** Set it as shown under [Requirements](#requirements), or pass `--api-key`.
`--dry-run` works without a key.

**`model '...' is not in the decision model catalog`.** The benchmark checks the id against OpenRouter's
`/api/v1/models?output_modalities=decisions` before the run, and the message lists the ids it found. Use one of
those, for example `typesafe/jev-1.13` or `microsoft/microsoft-decision-1`.

**The results name a dated model.** OpenRouter answers with the snapshot that served the request, for example
`typesafe/jev-1.13-20260917`. The benchmark treats that as the requested model.

## Notes on the models

- The local model is v1 of the Jev-style series. v2 (2B) reports better accuracy on typed decisions and on a
  programmatic threshold-policy test, and uses the same prompt format and letter readout, so it should drop in
  with `--model` (check with a `--limit 20` run first). v3 (0.8B) reads one verdict slot per option with its own
  `jev-score` scorer, so LM Studio's chat endpoint can't serve its decisions.
- The local model was trained on sentiment, NLI, topic, yes/no QA and rating tasks. Evaluating PLC expressions
  is outside its training data, which is exactly what this benchmark measures.
- Jev 1.13 and Microsoft-Decision-1 are hosted only. Microsoft-Decision-1 is post-trained from Qwen3.5 and is
  offered through Azure AI Foundry and OpenRouter; as of October 2026 neither model has weights you can load in
  LM Studio.
- OpenRouter lists more decision models at `/api/v1/models?output_modalities=decisions`. Any of them runs with
  `--provider decisions --model <id>`.
