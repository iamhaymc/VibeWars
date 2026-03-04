# VibeWars

A .NET 9 CLI tool that stages AI-powered debates between two autonomous chatbots on any topic. A third AI judge scores each round, suggests new angles, and declares a winner. The system features long-term memory that persists across sessions, injecting accumulated knowledge into future debates on related topics.

VibeWars supports two LLM providers --- **OpenRouter** (any model behind a unified API) and **AWS Bedrock** (Amazon Nova, Anthropic Claude, Meta Llama, and other models via the Converse API). Bots, judge, and auxiliary services can each use different providers and models.

---

## Table of Contents

- [Quick Start](#quick-start)
- [How It Works](#how-it-works)
- [Configuration](#configuration)
  - [Resolution Order](#resolution-order)
  - [Environment Variables](#environment-variables)
  - [CLI Flags](#cli-flags)
  - [YAML Configuration File](#yaml-configuration-file)
- [CLI Commands](#cli-commands)
- [Debate Formats](#debate-formats)
- [Debate Complexity Levels](#debate-complexity-levels)
- [Bot Personas](#bot-personas)
- [Human-in-the-Loop](#human-in-the-loop)
- [Memory System](#memory-system)
  - [Backends](#backends)
  - [Context Injection](#context-injection)
  - [Automatic Knowledge Summarization](#automatic-knowledge-summarization)
  - [Semantic Vector Search](#semantic-vector-search)
  - [Memory CLI Sub-commands](#memory-cli-sub-commands)
- [Analysis Features](#analysis-features)
  - [Fact-Checking](#fact-checking)
  - [Argument Graph](#argument-graph)
  - [Stance Evolution Tracking](#stance-evolution-tracking)
  - [Argument Strength Analytics](#argument-strength-analytics)
  - [Claim Survival Analysis](#claim-survival-analysis)
  - [Cross-Session Opinion Drift](#cross-session-opinion-drift)
- [Discovery Engine (Wave 3)](#discovery-engine-wave-3)
  - [Adversarial Strategy Engine](#adversarial-strategy-engine)
  - [Red Team Mode](#red-team-mode)
  - [Metacognitive Self-Reflection](#metacognitive-self-reflection)
  - [Dialectical Synthesis Arbiter](#dialectical-synthesis-arbiter)
  - [Adversarial Briefing](#adversarial-briefing)
  - [Hidden Objective Mode](#hidden-objective-mode)
- [Audience & Commentary](#audience--commentary)
  - [Audience Simulation](#audience-simulation)
  - [Spectator Commentary Bot](#spectator-commentary-bot)
  - [Debate Challenge Mechanics](#debate-challenge-mechanics)
- [Multi-Bot Features](#multi-bot-features)
  - [Judge Panel](#judge-panel)
  - [ELO Rating System](#elo-rating-system)
  - [Tournament Mode](#tournament-mode)
  - [Batch Mode](#batch-mode)
  - [Counterfactual Replay](#counterfactual-replay)
  - [Follow-Up Topic Chains](#follow-up-topic-chains)
- [Output & Integration](#output--integration)
  - [Web Streaming Dashboard](#web-streaming-dashboard)
  - [Debate Report Export](#debate-report-export)
  - [Podcast Script Export](#podcast-script-export)
  - [Webhook Integration](#webhook-integration)
  - [Slack Integration](#slack-integration)
  - [Discord Integration](#discord-integration)
- [Cost Tracking & Budget Guard](#cost-tracking--budget-guard)
- [Resilience](#resilience)
- [Architecture](#architecture)
- [Dependencies](#dependencies)
- [Building & Testing](#building--testing)
- [Supported Bedrock Models](#supported-bedrock-models)

---

## Quick Start

### Using OpenRouter

```bash
export OPENROUTER_API_KEY="sk-or-..."
dotnet run -- "Should AI replace human developers?"
```

Bot A and the Judge auto-detect OpenRouter. Bot B defaults to AWS Bedrock. Override with `--bot-b-provider openrouter`.

### Using AWS Bedrock

```bash
# AWS credentials from environment, ~/.aws/credentials, or EC2/ECS instance profile
dotnet run -- "Should AI replace human developers?"
```

### Full-Featured Example

```bash
dotnet run -- \
  --format structured \
  --complexity academic \
  --persona-a "Domain Expert" \
  --persona-b "Ethicist" \
  --fact-check \
  --argument-graph \
  --audience \
  --commentator \
  --analytics \
  --reflect \
  --elo \
  --post-debate-report \
  "Should nuclear energy be expanded?"
```

---

## How It Works

Each debate round consists of three turns:

1. **Bot A** responds to the topic (or Bot B's previous argument)
2. **Bot B** responds to Bot A's argument
3. **Judge** evaluates the exchange, picks a round winner, and suggests new angles

The judge's suggestions are fed back into both bots' conversation histories so the debate naturally evolves. After all rounds, the judge provides a final synthesis of the key insights, and the overall winner is determined by round wins.

### Debate Flow

```
                        Topic
                          |
            +-------------+-------------+
            |                           |
      Memory Search                Config Load
   (prior knowledge)           (CLI/env/YAML/defaults)
            |                           |
            +-------------+-------------+
                          |
                  System Prompts Built
         (persona + format + complexity + memory + briefing)
                          |
              +-----------+-----------+
              |     Debate Loop       |
              |  (1..maxRounds)       |
              |                       |
              |  Bot A argues         |
              |    -> fact-check      |
              |    -> analytics       |
              |    -> stance meter    |
              |    -> argument graph  |
              |    -> challenge detect|
              |    -> red team patch  |
              |                       |
              |  Bot B argues         |
              |    -> (same features) |
              |    -> red team attack |
              |                       |
              |  Judge evaluates      |
              |    -> self-reflection |
              |    -> strategy record |
              |    -> audience poll   |
              |    -> commentary      |
              |    -> budget check    |
              +-----------+-----------+
                          |
              +-----------+-----------+
              |  Post-Debate          |
              |  - Final synthesis    |
              |  - Arbiter synthesis  |
              |  - Red team scorecard |
              |  - Hidden obj detect  |
              |  - Follow-up chain    |
              |  - Session persist    |
              |  - ELO update         |
              |  - Embeddings         |
              |  - Webhook post       |
              +---------------------+
```

At the end of each run, all bot and judge messages are persisted to the active memory store as `MemoryEntry` records grouped under a `DebateSession`. These are surfaced as prior knowledge in future debates on related topics.

---

## Configuration

### Resolution Order

Configuration is resolved in this order (later sources override earlier ones):

1. **Built-in defaults** (e.g., `maxRounds: 3`, `debateFormat: freeform`)
2. **YAML config file** (`~/.vibewars/config.yml` or `--config <path>`)
3. **Named profile** from YAML (`--profile <name>`)
4. **Environment variables** (e.g., `OPENROUTER_API_KEY`, `MAX_ROUNDS`)
5. **CLI flags** (e.g., `--format structured`, `--max-cost-usd 0.50`)

### Environment Variables

#### Core Settings

| Variable | Default | Description |
|---|---|---|
| `OPENROUTER_API_KEY` | *(none)* | If set, Bot A and Judge auto-detect OpenRouter |
| `BOT_A_PROVIDER` | *(auto)* | `openrouter` or `bedrock` |
| `BOT_A_MODEL` | `openai/gpt-4o-mini` or `amazon.nova-lite-v1:0` | Model for Bot A |
| `BOT_B_PROVIDER` | `bedrock` | `openrouter` or `bedrock` |
| `BOT_B_MODEL` | `amazon.nova-lite-v1:0` | Model for Bot B |
| `JUDGE_PROVIDER` | *(auto)* | `openrouter` or `bedrock` |
| `JUDGE_MODEL` | `openai/gpt-4o-mini` or `amazon.nova-lite-v1:0` | Model for the Judge |
| `AWS_REGION` | `us-east-1` | AWS region for Bedrock |
| `MAX_ROUNDS` | `3` | Number of debate rounds |
| `VIBEWARS_FORMAT` | `freeform` | Debate format |

#### Persona Settings

| Variable | Default | Description |
|---|---|---|
| `BOT_A_PERSONA` | *(none)* | Built-in name or `custom` |
| `BOT_B_PERSONA` | *(none)* | Built-in name or `custom` |
| `BOT_A_PERSONA_DESC` | *(none)* | Custom persona description |
| `BOT_B_PERSONA_DESC` | *(none)* | Custom persona description |

#### Memory System

| Variable | Default | Description |
|---|---|---|
| `VIBEWARS_MEMORY_BACKEND` | `sqlite` | `sqlite`, `s3`, or `hybrid` |
| `VIBEWARS_DB_PATH` | `~/.vibewars/memory.db` | SQLite database path |
| `VIBEWARS_S3_BUCKET` | *(required for s3/hybrid)* | S3 bucket name |
| `VIBEWARS_S3_PREFIX` | `vibewars/` | S3 key prefix |
| `VIBEWARS_S3_CACHE_SIZE` | `20` | In-memory LRU cache size for S3 |
| `VIBEWARS_S3_SELECT` | `false` | Use S3 Select for server-side filtering |
| `VIBEWARS_MEMORY_CONTEXT_TOKENS` | `500` | Max tokens of prior knowledge injected |
| `VIBEWARS_MEMORY_TOP_K` | `10` | Past entries retrieved per search |
| `VIBEWARS_SUMMARIZE_THRESHOLD` | `10` | Entry count that triggers auto-summarization |
| `VIBEWARS_EMBED_BACKEND` | `none` | `openrouter`, `bedrock`, or `none` |
| `VIBEWARS_EMBED_MODEL` | `openai/text-embedding-3-small` | Embedding model |

#### Feature Flags

| Variable | Default | Description |
|---|---|---|
| `VIBEWARS_FACT_CHECK` | `false` | Per-turn fact-checking |
| `VIBEWARS_FACT_CHECKER_MODEL` | *(judge model)* | Dedicated fact-check model |
| `VIBEWARS_ARGUMENT_GRAPH` | `false` | Argument graph extraction |
| `VIBEWARS_STANCE_TRACKING` | `false` | Stance evolution tracking |
| `VIBEWARS_JUDGE_PANEL` | *(none)* | Comma-separated `provider:model` pairs |
| `VIBEWARS_MAX_COST_USD` | *(none)* | Budget limit in USD |
| `VIBEWARS_RETRY_MAX` | `4` | Max retry attempts |
| `VIBEWARS_RETRY_BASE_DELAY_MS` | `1000` | Base delay for exponential backoff |
| `VIBEWARS_AUDIENCE` | `false` | Audience simulation |
| `VIBEWARS_AUDIENCE_SPLIT` | `50/50` | Starting audience split |
| `VIBEWARS_COMMENTATOR` | `false` | Commentary bot |
| `VIBEWARS_COMMENTATOR_STYLE` | `sports` | `sports`, `academic`, `snarky`, `dramatic`, `dry` |
| `VIBEWARS_COMMENTATOR_MODEL` | *(judge model)* | Commentator model |
| `VIBEWARS_CHALLENGES` | `false` | Debate challenge mechanics |
| `VIBEWARS_COMPLEXITY` | `standard` | `casual`, `standard`, `academic`, `technical`, `policybrief` |
| `VIBEWARS_STRATEGY` | `false` | Adversarial strategy engine |
| `VIBEWARS_REFLECT` | `false` | Self-reflection layer |
| `VIBEWARS_ARBITER` | `false` | Dialectical synthesis arbiter |
| `VIBEWARS_BRIEF` | `false` | Adversarial briefing injection |
| `VIBEWARS_ANALYTICS` | `false` | Argument strength scoring |
| `VIBEWARS_NO_ELO` | `false` | Disable ELO tracking |
| `VIBEWARS_WEBHOOK_URL` | *(none)* | Webhook endpoint URL |
| `VIBEWARS_WEBHOOK_PROVIDER` | `generic` | `discord`, `slack`, `teams`, `generic` |
| `VIBEWARS_WEBHOOK_ON_COMPLETE` | `false` | Post summary after debate |
| `VIBEWARS_WEBHOOK_ON_ROUND` | `false` | Post summary after each round |
| `VIBEWARS_HIDDEN_OBJ_A` | *(none)* | Hidden objective for Bot A |
| `VIBEWARS_HIDDEN_OBJ_B` | *(none)* | Hidden objective for Bot B |
| `VIBEWARS_REVEAL_OBJECTIVES` | `false` | Show hidden objectives at debate end |

### CLI Flags

```
dotnet run -- [flags] [topic]
```

| Flag | Description |
|---|---|
| `--no-stream` | Buffer responses instead of streaming word-by-word |
| `--no-tui` | Plain-text output (auto-enabled when stdout is redirected) |
| `--no-memory` | Skip memory injection for this run |
| `--dry-run` | Validate config and preview prompts without API calls |
| `--human <role>` | Participate as `A`, `B`, or `judge` |
| `--think-time <secs>` | Pause before prompting human input |
| `--persona-a <name>` | Bot A persona |
| `--persona-b <name>` | Bot B persona |
| `--bot-a-provider <p>` | `openrouter` or `bedrock` |
| `--bot-b-provider <p>` | `openrouter` or `bedrock` |
| `--judge-provider <p>` | `openrouter` or `bedrock` |
| `--format <name>` | `freeform`, `structured`, `oxford`, `socratic`, `collaborative`, `redteam` |
| `--complexity <level>` | `casual`, `standard`, `academic`, `technical`, `policybrief` |
| `--max-cost-usd <amount>` | Budget limit in USD |
| `--cost-hard-stop` | Stop immediately when budget is exceeded (skip post-judge features) |
| `--cost-interactive` | Prompt to continue when budget is exceeded |
| `--fact-check` | Enable fact-checking |
| `--argument-graph` | Enable argument graph extraction |
| `--stance-tracking` | Enable stance evolution tracking |
| `--audience` | Enable audience simulation |
| `--audience-split <a/b>` | Starting split, e.g., `40/60` |
| `--commentator` | Enable commentary bot |
| `--commentator-model <m>` | Model for the commentator |
| `--commentator-style <s>` | `sports`, `academic`, `snarky`, `dramatic`, `dry` |
| `--challenges` | Enable formal debate interruptions |
| `--strategy` | Enable adversarial strategy engine |
| `--red-team` | Enable red team mode (auto-sets `--format redteam`) |
| `--proposal <text>` | Bot A's initial position (for red team format) |
| `--reflect` | Enable self-reflection layer |
| `--arbiter` | Enable dialectical synthesis arbiter |
| `--brief` | Enable adversarial briefing injection |
| `--analytics` | Enable argument strength scoring |
| `--elo` / `--no-elo` | Enable/disable ELO tracking |
| `--chain` | Generate follow-up topic chain |
| `--chain-depth <n>` | Depth of follow-up chain (default: 3) |
| `--hidden-objective-a <t>` | Secret directive for Bot A |
| `--hidden-objective-b <t>` | Secret directive for Bot B |
| `--reveal-objectives` | Show hidden objectives at debate end |
| `--web [port]` | Start web dashboard (default port: 5050) |
| `--no-browser` | Don't auto-open browser for `--web` |
| `--webhook-url <url>` | Webhook endpoint |
| `--webhook-provider <p>` | `discord`, `slack`, `teams`, `generic` |
| `--webhook-on-complete` | Post summary after debate |
| `--webhook-on-round` | Post summary after each round |
| `--post-debate-report` | Auto-generate HTML report |
| `--config <path>` | Path to YAML config file |
| `--profile <name>` | Named profile from YAML |

### YAML Configuration File

```bash
# Create a starter config
dotnet run -- config init

# Validate resolved configuration (shows all settings and active features)
dotnet run -- config validate
```

Example `~/.vibewars/config.yml`:

```yaml
openRouterApiKey: "sk-or-..."
maxRounds: 5
botAPersona: Pragmatist
botBPersona: Idealist
debateFormat: structured
complexity: academic
memoryBackend: sqlite
dbPath: /data/vibewars/debates.db

profiles:
  high-quality:
    botAProvider: openrouter
    botAModel: openai/gpt-4o
    botBProvider: openrouter
    botBModel: anthropic/claude-3-5-sonnet
    maxRounds: 5
  cheap:
    botAModel: openai/gpt-4o-mini
    botBModel: amazon.nova-micro-v1:0
    maxRounds: 2
```

---

## CLI Commands

| Command | Description |
|---|---|
| `dotnet run -- "topic"` | Run a debate |
| `dotnet run -- tournament "topic"` | Run a single-elimination tournament |
| `dotnet run -- batch topics.txt` | Run debates from a file |
| `dotnet run -- replay <sessionId>` | Replay with different models/personas |
| `dotnet run -- replay list` | List all replay sessions |
| `dotnet run -- memory <sub>` | Memory management (see sub-commands below) |
| `dotnet run -- persona list` | List available personas |
| `dotnet run -- config init` | Create starter YAML config |
| `dotnet run -- config validate` | Show resolved configuration |
| `dotnet run -- elo` | Show ELO leaderboard |
| `dotnet run -- elo history <id>` | Show ELO rating history with sparkline |
| `dotnet run -- webhook test` | Test webhook connectivity |

---

## Debate Formats

Select with `--format <name>` or `VIBEWARS_FORMAT`. Each format changes the system prompts and per-turn instructions given to the bots.

| Format | Description |
|---|---|
| `freeform` | Unrestricted debate --- each bot argues freely (default) |
| `structured` | Round 1: Claim-Evidence-Warrant. Later rounds: Steelman-Rebuttal-Advance. Final round: Synthesis |
| `oxford` | Bot A proposes the motion, Bot B opposes. Modeled on the [Oxford Union](https://en.wikipedia.org/wiki/Oxford_Union) debating format |
| `socratic` | Bots ask probing questions rather than making assertions. Inspired by the [Socratic method](https://en.wikipedia.org/wiki/Socratic_method) of inquiry through dialogue |
| `collaborative` | Bots co-author a shared position, refining it each round. Judge acts as editor |
| `redteam` | Bot A proposes and defends. Bot B attacks, probing for weaknesses and edge cases. Auto-enabled by `--red-team` |

**Background --- Structured Debate:** The Claim-Evidence-Warrant structure comes from the [Toulmin model of argumentation](https://en.wikipedia.org/wiki/Toulmin_model). A claim is the assertion, evidence is the supporting data, and the warrant is the logical bridge explaining why the evidence supports the claim. The steelman technique strengthens the opponent's argument before rebutting it, preventing strawman fallacies.

---

## Debate Complexity Levels

Control the intellectual register with `--complexity <level>`:

| Level | Effect on Bots | Effect on Judge |
|---|---|---|
| `casual` | Conversational, jargon-free, 2-3 sentences max | Standard evaluation |
| `standard` | Default behavior (3-5 sentences) | Standard evaluation |
| `academic` | Formal register, citation-style references, Claim-Evidence-Warrant, 4-6 sentences | Evaluates citation quality and rigor |
| `technical` | Expert audience, precise terminology, quantified claims | Standard evaluation |
| `policybrief` | Policy recommendations, stakeholders, trade-offs, 5-8 sentences | Evaluates stakeholder consideration and feasibility |

---

## Bot Personas

Select with `--persona-a <name>` / `--persona-b <name>`. List all with `dotnet run -- persona list`.

| Persona | Archetype | Strength | Weakness |
|---|---|---|---|
| Pragmatist | Pragmatist | Favors data, outcomes, feasibility | May overlook ethical dimensions |
| Idealist | Idealist | Inspires transformative thinking | May underestimate practical constraints |
| Devil's Advocate | DevilsAdvocate | Exposes weaknesses and blind spots | May appear contrarian |
| Domain Expert | DomainExpert | Technically rigorous and specific | May use too much jargon |
| Empiricist | Empiricist | Grounds debate in verifiable facts | May dismiss valid intuitions |
| Ethicist | Ethicist | Surfaces moral dimensions | May prioritize purity over outcomes |
| Contrarian | Custom | Surfaces unconventional insights | May reject valid consensus |
| Synthesizer | Custom | Produces integrated positions | May prematurely reconcile views |

**Custom persona:** Set `--persona-a custom` with `BOT_A_PERSONA_DESC="Your full persona description here"`.

---

## Human-in-the-Loop

```bash
dotnet run -- --human A "topic"       # Play as Bot A
dotnet run -- --human B "topic"       # Play as Bot B
dotnet run -- --human judge "topic"   # Act as judge
dotnet run -- --human A --think-time 10 "topic"  # 10-sec reading time
```

Press Enter with no input to auto-generate the AI response instead.

---

## Memory System

### Backends

**SQLite (default)** --- Local database with FTS5 full-text search and optional vector embeddings. No infrastructure needed.

```bash
export VIBEWARS_MEMORY_BACKEND=sqlite
export VIBEWARS_DB_PATH=/custom/path.db   # optional
```

**Background --- FTS5:** SQLite's [FTS5 extension](https://www.sqlite.org/fts5.html) is a full-text search engine built into SQLite. It tokenizes stored text and builds an inverted index, enabling fast keyword queries. When an FTS5 query fails (e.g., due to special characters), VibeWars falls back to SQL `LIKE` with properly escaped wildcards.

**S3** --- Each session stored as a JSON object in AWS S3. Requires `VIBEWARS_S3_BUCKET`. Uses an in-memory LRU cache to avoid redundant downloads.

```bash
export VIBEWARS_MEMORY_BACKEND=s3
export VIBEWARS_S3_BUCKET=my-bucket
```

Required IAM permissions: `s3:PutObject`, `s3:GetObject`, `s3:ListBucket`, `s3:DeleteObject`.

**Hybrid** --- Writes to both SQLite (fast local) and S3 (durable archive). S3 writes are fire-and-forget. Reads prefer SQLite; `GetSessionEntries` falls back to S3 when a session isn't found locally. All SQLite-dependent features (ELO, drift, strategy, embeddings, auto-summarization) work with hybrid backend.

```bash
export VIBEWARS_MEMORY_BACKEND=hybrid
export VIBEWARS_S3_BUCKET=my-bucket
```

### Context Injection

Before each debate, VibeWars queries the memory store for the most relevant past entries matching the topic. These are formatted as a "Prior knowledge" block and appended to each bot's system prompt:

```
Prior knowledge from past debates on related topics:
- [2025-06-01] Bot A on "AI ethics": "Transparency in model training is..."
- [2025-06-03] Bot B on "AI regulation": "Regulatory sandboxes allow..."
```

The block is capped at `VIBEWARS_MEMORY_CONTEXT_TOKENS` (default 500, ~2000 characters). Disable with `--no-memory`.

### Automatic Knowledge Summarization

When the number of stored entries for a topic reaches `VIBEWARS_SUMMARIZE_THRESHOLD` (default 10), the judge model distills them into a 3-5 sentence canonical summary. This summary is stored and preferentially surfaced in future context injection.

### Semantic Vector Search

For queries that go beyond keyword matching, enable dense-embedding retrieval:

```bash
export VIBEWARS_EMBED_BACKEND=openrouter   # or bedrock
dotnet run -- memory reindex               # back-fill existing entries
```

**Background --- Cosine Similarity:** Embeddings are high-dimensional vectors that capture semantic meaning. [Cosine similarity](https://en.wikipedia.org/wiki/Cosine_similarity) measures the angle between two vectors --- values near 1.0 mean the texts are semantically similar, regardless of exact wording. This enables finding past arguments about "carbon emissions" when searching for "greenhouse gases."

### Memory CLI Sub-commands

```
dotnet run -- memory <sub-command>
```

| Sub-command | Description |
|---|---|
| `list [n]` | Print last N sessions (default 10) with Format column |
| `show <sessionId>` | Print all entries for a session |
| `search <query>` | Full-text search across all entries |
| `export <sessionId> [--format json\|csv]` | Dump to stdout |
| `report <sessionId> [--format html\|md\|json\|podcast] [--out <path>]` | Generate report |
| `graph <sessionId> [--format mermaid\|dot]` | Render argument graph |
| `graph-stats <sessionId>` | Argument graph statistics |
| `follow-ups <topic>` | Stored follow-up suggestions |
| `stance <topic>` | Stance entries across sessions |
| `drift <topic>` | Opinion drift analysis |
| `drift-compare <topic> <model-a> <model-b>` | Compare drift trajectories |
| `analytics <sessionId>` | Argument strength heatmap |
| `reflections <sessionId>` | Self-reflection timeline |
| `strategies <contestant-id>` | Tactic win rates |
| `autopsy <sessionId>` | Claim survival analysis |
| `brief-impact <topic>` | Briefed vs. un-briefed comparison |
| `reindex` | Back-fill embeddings |
| `clear [--confirm]` | Delete all memories |

---

## Analysis Features

### Fact-Checking

```bash
dotnet run -- --fact-check "Is nuclear energy safe?"
```

A dedicated model audits every claim each turn, rating confidence as `HIGH`, `MEDIUM`, or `LOW`. Low-confidence flags are injected into the opposing bot's next prompt, encouraging them to challenge weak claims.

**Background --- Epistemic Calibration:** Fact-checking in VibeWars is not about accessing a ground-truth database --- it uses a separate LLM to assess the internal consistency and plausibility of claims. This is closer to [epistemic calibration](https://en.wikipedia.org/wiki/Calibrated_probability_assessment) than traditional fact-checking.

### Argument Graph

```bash
dotnet run -- --argument-graph "Should AI be regulated?"
dotnet run -- memory graph <sessionId>              # Mermaid diagram
dotnet run -- memory graph <sessionId> --format dot # Graphviz DOT
dotnet run -- memory graph-stats <sessionId>        # Statistics
```

Each turn's claims are extracted and typed (Assertion, Evidence, Rebuttal, Concession, Question, Synthesis). Relations between claims across turns are mapped (Supports, Challenges, Extends, Answers, Concedes).

**Background --- Argumentation Frameworks:** The claim-relation model is inspired by [Dung's abstract argumentation frameworks](https://en.wikipedia.org/wiki/Argumentation_framework), where arguments form a directed graph and "attack" relations determine which arguments are defeated.

### Stance Evolution Tracking

```bash
dotnet run -- --stance-tracking "Is capitalism the best economic system?"
```

Each turn, an LLM assigns a stance score from -5 (strongly oppose) to +5 (strongly support) and identifies concessions. At debate end, an ASCII stance bar and **Intellectual Progress Score** are displayed.

**Background --- Intellectual Progress Score:** IPS measures whether a debate produced genuine intellectual movement: `(deltaA + deltaB + concessions * 0.5) / maxRounds`. Higher scores indicate bots engaged with opposing arguments rather than talking past each other. This operationalizes [deliberative democracy](https://en.wikipedia.org/wiki/Deliberative_democracy) ideals.

### Argument Strength Analytics

```bash
dotnet run -- --analytics "The ethics of gene editing"
dotnet run -- memory analytics <sessionId>
```

Each argument is scored on three dimensions (0-10): **Logical Rigor** (internal consistency, absence of fallacies), **Novelty** (new information vs. prior arguments), and **Persuasive Impact** (likely to shift a neutral observer). The composite score is a weighted average: `0.4 * rigor + 0.3 * novelty + 0.3 * impact`.

Results are rendered as a block-character heatmap (`░▒▓█`).

### Claim Survival Analysis

```bash
dotnet run -- memory autopsy <sessionId>
```

Post-debate analysis classifying every claim as Active, Challenged, Defended, Refuted, Conceded, or Abandoned. Renders an **Argument Graveyard** (refuted/conceded claims) and **Survivors** list. Requires `--argument-graph` during the debate.

### Cross-Session Opinion Drift

```bash
dotnet run -- memory drift "artificial intelligence"
dotnet run -- memory drift-compare "AI ethics" gpt-4o nova-lite
```

Tracks how model positions evolve across multiple debate sessions on the same topic.

**Background --- Drift Velocity:** Computed as `total_stance_change / number_of_sessions`. Trend labels: **Stable** (|vel| < 0.5), **Converging** (vel < -0.5, positions approaching center), **Diverging** (vel > 0.5, positions moving apart).

---

## Discovery Engine (Wave 3)

### Adversarial Strategy Engine

```bash
dotnet run -- --strategy "Should AI be regulated?"
dotnet run -- memory strategies <contestant-id>
```

Before each turn, the strategy engine reviews opponent history and selects the highest-leverage rhetorical tactic. Historical tactic win rates (persisted in SQLite) influence future selections.

**Background --- Game-Theoretic Debate:** This implements a simplified form of [iterated game theory](https://en.wikipedia.org/wiki/Iterated_prisoner%27s_dilemma) where agents learn which strategies work against specific opponents over time, creating emergent strategic adaptation.

### Red Team Mode

```bash
dotnet run -- --red-team --proposal "Migrate all infrastructure to serverless" "Cloud architecture"
```

`--red-team` automatically sets the debate format to RedTeam and enables vulnerability tracking. Bot A proposes and defends. Bot B systematically attacks, discovering weaknesses. A vulnerability scorecard tracks each finding's lifecycle: Open, Patched, Disputed, or Accepted.

**Background --- Red Teaming:** Borrowed from [military/security red teaming](https://en.wikipedia.org/wiki/Red_team), this mode stress-tests ideas rather than arguing for a winner. The vulnerability categories (LogicGap, ImplementationRisk, EthicalBlindSpot, AdversarialExploit, SystemicFailure, HiddenAssumption, EdgeCase, UnintendedConsequence) mirror real-world threat modeling.

### Metacognitive Self-Reflection

```bash
dotnet run -- --reflect "The future of remote work"
dotnet run -- memory reflections <sessionId>
```

After each round, bots evaluate their own performance: strongest point, weakest response, and planned improvement. The `PlannedImprovement` is privately injected into the bot's next-round system prompt.

**Background --- Metacognition:** [Metacognition](https://en.wikipedia.org/wiki/Metacognition) is "thinking about thinking." In LLM terms, this implements a self-critique loop where the model reflects on its own output quality and adjusts strategy, similar to [Constitutional AI](https://arxiv.org/abs/2212.08073) self-correction.

### Dialectical Synthesis Arbiter

```bash
dotnet run -- --arbiter "Should social media be regulated?"
```

After the debate, a separate LLM plays the [Hegelian dialectical](https://en.wikipedia.org/wiki/Dialectic#Hegelian_dialectic) synthesizer: extracting the core defensible **thesis**, the core defensible **antithesis**, and forging a **synthesis** that genuinely integrates both. The synthesis must transcend both positions by resolving the underlying contradiction. Residual **open questions** that neither position could settle are identified.

### Adversarial Briefing

```bash
dotnet run -- --brief "Universal healthcare"
dotnet run -- memory brief-impact <topic>
```

Before each debate, retrieves the most effective past counter-arguments from memory and injects them into each bot's system prompt as an intelligence briefing. Requires at least 3 prior debate argument entries for the topic.

### Hidden Objective Mode

```bash
dotnet run -- \
  --hidden-objective-a "End every turn with a rhetorical question" \
  --hidden-objective-b "Never use the word however" \
  --reveal-objectives "Climate change"
```

Secret directives injected into each bot's private system prompt. The judge attempts to detect them post-debate, rating execution quality 1-10. Nine built-in objectives across five categories: Rhetorical, Epistemic, Strategic, Social, and Meta.

---

## Audience & Commentary

### Audience Simulation

```bash
dotnet run -- --audience "Should AI be regulated?"
dotnet run -- --audience --audience-split 40/60 "The future of work"
```

A virtual audience shifts support after each round. The judge model evaluates how the exchange affected audience sentiment. Results are displayed as a live poll bar:

```
Audience Poll  ----------------------------------------
Bot A     ███████████████░░░░░░░░░░░░░░░░░  45%
Bot B     ░░░░░░░░░░░░░░░███████████████████  55%
  Mood: skeptical
```

### Spectator Commentary Bot

```bash
dotnet run -- --commentator "Should nuclear energy be expanded?"
dotnet run -- --commentator --commentator-style academic "The trolley problem"
```

A dedicated LLM provides 1-2 sentence live commentary after each exchange, noting rhetorical moves and building suspense.

| Style | Personality |
|---|---|
| `sports` | Enthusiastic announcer, dramatic pauses, hype (default) |
| `academic` | Formal moderator, references rhetorical frameworks |
| `snarky` | Sharp-tongued critic, witty and occasionally sarcastic |
| `dramatic` | Theatrical critic, vivid metaphors, operatic emotions |
| `dry` | Deadpan, understated, minimal words |

### Debate Challenge Mechanics

```bash
dotnet run -- --challenges "Should AI be open source?"
```

After each bot speaks, the judge evaluates whether the argument contains challengeable claims. Valid challenges generate a **POINT OF ORDER** injected into the opponent's next prompt, requiring them to address the challenge before advancing their argument.

Challenge types: `CitationNeeded`, `PointOfOrder`, `PersonalFoul`, `PointOfInformation`, `DirectChallenge`.

**Background --- Parliamentary Debate:** Challenge mechanics are inspired by [parliamentary procedure](https://en.wikipedia.org/wiki/Parliamentary_procedure), where Points of Order and Points of Information are formal interruption mechanisms that keep debate rigorous.

---

## Multi-Bot Features

### Judge Panel

```bash
export VIBEWARS_JUDGE_PANEL="openrouter:openai/gpt-4o,bedrock:amazon.nova-pro-v1:0,openrouter:anthropic/claude-3-5-haiku"
dotnet run -- "Should AI be open source?"
```

Replace the single judge with a panel. All panelists evaluate in parallel. Individual verdicts are displayed, then aggregated by majority vote. This reduces single-model bias.

### ELO Rating System

```bash
dotnet run -- elo                       # Top 20 leaderboard
dotnet run -- elo leaderboard 50        # Top 50
dotnet run -- elo history <contestant>  # Rating history with sparkline
```

Every debate updates ELO ratings for both contestants. Contestants are identified as `provider/model/persona` (e.g., `OpenRouter/openai/gpt-4o-mini/Pragmatist`).

**Background --- ELO Rating:** The [ELO rating system](https://en.wikipedia.org/wiki/Elo_rating_system) was created by Arpad Elo for chess. Each player has a numerical rating; after a match, the winner gains points and the loser loses points, with the magnitude determined by the expected outcome. An upset (low-rated player beats high-rated) produces a larger swing. VibeWars uses K=32 for single debates and K=16 for tournament matches. Contestants with fewer than 5 matches are marked as "unrated."

Disable with `--no-elo`.

### Tournament Mode

```bash
dotnet run -- tournament "The future of work"
dotnet run -- tournament "AI ethics" --contestants contestants.csv
```

Single-elimination bracket with live rendering. Custom contestants via CSV (`name,provider,model,persona`). All matches use resilient clients with retry policies. The Swiss and Round-Robin tournament engines are also available as library code.

### Batch Mode

```bash
dotnet run -- batch topics.txt                 # Sequential
dotnet run -- batch topics.txt --parallel 3    # 3 concurrent debates
```

Topics file: one topic per line (lines starting with `#` are ignored). Results summarized in a table.

### Counterfactual Replay

```bash
dotnet run -- replay <sessionId> --bot-a-model gpt-4o --bot-b-persona Ethicist
dotnet run -- replay list
```

Load any stored session and re-run with different models or personas. Produces a side-by-side **Counterfactual Comparison Report** showing round-by-round differences and whether the overall outcome changed.

### Follow-Up Topic Chains

```bash
dotnet run -- --chain "The future of AI"
dotnet run -- --chain --chain-depth 5 "Climate change policy"
dotnet run -- memory follow-ups "artificial intelligence"
```

After each debate, the judge generates 3-5 follow-up topics. With `--chain`, the system iterates `--chain-depth` times (default 3), each iteration feeding the first topic back as a new synthesis to generate progressively more specific follow-ups. All follow-up topics are persisted for later retrieval.

---

## Output & Integration

### Web Streaming Dashboard

```bash
dotnet run -- --web "topic"                # Port 5050
dotnet run -- --web 8080 "topic"           # Custom port
dotnet run -- --web --no-browser "topic"   # No auto-open
```

Two-column live debate view with scoreboard. Uses [Server-Sent Events (SSE)](https://developer.mozilla.org/en-US/docs/Web/API/Server-sent_events) for real-time streaming.

### Debate Report Export

```bash
dotnet run -- memory report <id>                           # Markdown
dotnet run -- memory report <id> --format html --out r.html  # HTML
dotnet run -- memory report <id> --format json             # JSON
dotnet run -- memory report <id> --format podcast          # Podcast script
dotnet run -- --post-debate-report "topic"                 # Auto-generate
```

HTML reports include a dark-themed responsive layout with sticky header, two-column exchange view, judge verdicts, final synthesis, and fact-check summary. Markdown reports include YAML front matter with topic, date, winner, format, complexity, token count, and estimated cost.

### Podcast Script Export

```bash
dotnet run -- memory report <sessionId> --format podcast --out episode.txt
```

Generates a production-ready podcast script with show title, participants, runtime estimate, opening/closing music cues, transition stings, speaker lines, fact-check appendix, and show notes.

### Webhook Integration

```bash
export VIBEWARS_WEBHOOK_URL="https://hooks.slack.com/services/..."
export VIBEWARS_WEBHOOK_PROVIDER="slack"
export VIBEWARS_WEBHOOK_ON_COMPLETE=true
dotnet run -- "topic"

dotnet run -- webhook test   # Test connectivity
```

| Provider | Format |
|---|---|
| `discord` | Rich embed with color-coded fields |
| `slack` | Block Kit message with sections |
| `teams` | Adaptive Card |
| `generic` | JSON payload |

### Slack Integration

1. Go to [api.slack.com/apps](https://api.slack.com/apps) > Create New App > From scratch
2. Enable Incoming Webhooks and add to your workspace
3. Set `VIBEWARS_WEBHOOK_URL` and `VIBEWARS_WEBHOOK_PROVIDER=slack`

### Discord Integration

1. Channel Settings > Integrations > Webhooks > New Webhook
2. Copy Webhook URL
3. Set `VIBEWARS_WEBHOOK_URL` and `VIBEWARS_WEBHOOK_PROVIDER=discord`

---

## Cost Tracking & Budget Guard

Token usage and estimated USD cost are tracked per session:

```bash
dotnet run -- --max-cost-usd 0.50 "topic"              # Hard limit
dotnet run -- --max-cost-usd 0.50 --cost-interactive "topic"  # Prompt to continue
dotnet run -- --max-cost-usd 0.50 --cost-hard-stop "topic"    # Skip post-judge features
```

- `--max-cost-usd`: abort debate when estimated cost exceeds the limit
- `--cost-interactive`: prompt the user to continue or stop when the limit is reached
- `--cost-hard-stop`: skip remaining post-judge features (reflection, audience, commentary) immediately

**Note:** Streaming mode (the default) does not receive token counts from the API. Use `--no-stream` for accurate cost tracking.

---

## Resilience

VibeWars wraps all LLM clients in a `ResilientChatClient` using [Polly](https://github.com/App-vNext/Polly) for:

- **Exponential backoff with jitter** on transient errors (429 rate limits, 5xx server errors)
- **Circuit breaker** that temporarily halts requests after repeated failures
- Configurable via `VIBEWARS_RETRY_MAX` (default 4) and `VIBEWARS_RETRY_BASE_DELAY_MS` (default 1000)

All optional features (audience, commentator, analytics, etc.) use `try/catch` with graceful degradation --- if an auxiliary LLM call fails, the debate continues without that feature's output.

---

## Architecture

```
VibeWars/
├── Program.cs                        # Entry point, debate loop, CLI commands
├── Ansi.cs                           # ANSI color/style constants
├── Clients/
│   ├── IChatClient.cs                # Chat provider interface
│   ├── OpenRouterClient.cs           # OpenRouter HTTP + SSE implementation
│   ├── BedrockClient.cs              # AWS Bedrock Converse API
│   ├── ResilientChatClient.cs        # Polly retry + circuit-breaker wrapper
│   ├── IEmbeddingClient.cs           # Embedding provider interface
│   ├── OpenRouterEmbeddingClient.cs  # OpenRouter embeddings
│   ├── BedrockEmbeddingClient.cs     # Bedrock Titan embeddings
│   ├── EmbeddingHelper.cs            # Embedding client factory + cosine similarity
│   ├── IMemoryStore.cs               # Memory store abstraction
│   ├── SqliteMemoryStore.cs          # SQLite + FTS5 + vector embeddings
│   ├── S3MemoryStore.cs              # AWS S3 with LRU cache
│   └── HybridMemoryStore.cs          # SQLite + S3 hybrid
├── Config/
│   ├── VibeWarsConfig.cs             # All 47 configuration properties
│   ├── VibeWarsConfigOverride.cs     # Nullable overlay for YAML merge
│   └── ConfigLoader.cs               # CLI -> env -> YAML -> defaults merge
├── Models/
│   ├── ChatModels.cs                 # ChatMessage, JudgeVerdict, MemoryEntry, DebateSession
│   ├── BotPersona.cs                 # Persona record and archetype enum
│   ├── DebateFormat.cs               # DebateFormat enum and helpers
│   ├── DebateStrategy.cs             # Strategy and StrategyRecord records
│   ├── ArgumentGraph.cs              # ArgumentNode, ArgumentEdge, enums
│   └── TokenUsage.cs                 # Token counts, cost, CostAccumulator
├── Personas/
│   └── PersonaLibrary.cs             # 8 built-in personas + custom resolution
├── TUI/
│   └── SpectreRenderer.cs            # Spectre.Console panels, banner, live updates
├── HumanPlayer/
│   └── HumanInputReader.cs           # Console input for human-in-the-loop
├── JudgePanel/
│   └── JudgePanelService.cs          # Multi-judge aggregation
├── FactChecker/
│   ├── FactCheckerService.cs         # Per-claim confidence rating
│   └── FactCheckResult.cs            # Claim records
├── ArgumentGraph/
│   ├── ArgumentGraphService.cs       # Claim extraction, Mermaid/DOT export
│   └── ClaimSurvivalAnalyzer.cs      # Claim lifecycle tracking
├── StanceTracker/
│   └── StanceMeter.cs                # Stance metering, IPS calculation
├── Analytics/
│   ├── ArgumentStrengthScorer.cs     # Rigor/novelty/impact scoring
│   └── HeatmapRenderer.cs            # Block-character heatmap
├── Strategy/
│   └── StrategyEngine.cs             # Tactic selection and outcome tracking
├── RedTeam/
│   └── VulnerabilityTracker.cs       # Vulnerability lifecycle tracking
├── Reflection/
│   └── SelfReflectionService.cs      # Metacognitive self-evaluation
├── Arbiter/
│   └── DialecticalArbiter.cs         # Hegelian synthesis engine
├── Memory/
│   └── AdversarialBriefingService.cs # Historical counter-argument injection
├── Audience/
│   └── AudienceSimulator.cs          # Virtual audience polling
├── Commentator/
│   └── CommentatorService.cs         # 5-style commentary bot
├── Challenges/
│   └── ChallengeService.cs           # Formal interruption mechanics
├── Drift/
│   └── OpinionDriftService.cs        # Cross-session stance analysis
├── Complexity/
│   └── DebateComplexityService.cs    # 5 complexity levels
├── HiddenObjective/
│   └── ObjectiveLibrary.cs           # 9 built-in objectives + detector
├── Elo/
│   └── EloService.cs                 # ELO ratings, leaderboard, history
├── Tournament/
│   ├── TournamentBracket.cs          # Single-elimination bracket
│   ├── SwissTournament.cs            # Swiss pairing + Buchholz tiebreaking
│   ├── RoundRobinTournament.cs       # Circle-method scheduling
│   ├── TournamentContestant.cs       # Contestant record
│   └── TournamentFormat.cs           # Format enum
├── FollowUp/
│   └── FollowUpService.cs            # Follow-up topic generation
├── Replay/
│   └── CounterfactualReplayService.cs # Session replay with config overrides
├── Reports/
│   ├── DebateReportGenerator.cs      # HTML, Markdown, JSON, Podcast
│   └── PodcastScriptGenerator.cs     # Podcast script with stage directions
├── Web/
│   └── WebDashboardService.cs        # Embedded HTTP + SSE dashboard
├── Webhook/
│   └── WebhookService.cs             # Discord/Slack/Teams/Generic posting
├── Notifications/
│   ├── SlackNotifier.cs              # Block Kit payloads
│   └── DiscordNotifier.cs            # Discord embed payloads
├── Resilience/
│   └── ResilienceHelper.cs           # Polly pipeline configuration
└── Scripted/
    └── ScriptedDebate.cs             # Deterministic test client
```

### SQLite Schema

The database contains these tables (auto-migrated on startup):

| Table | Purpose |
|---|---|
| `SchemaVersion` | Tracks migration version (currently v3) |
| `DebateSessions` | SessionId, Topic, StartedAt, EndedAt, OverallWinner, FinalSynthesis, Format, TotalTokens, EstimatedCostUsd, Complexity |
| `MemoryEntries` | Id, BotName, Topic, Round, Role, Content, Timestamp, Tags, SessionId, Embedding (nullable BLOB) |
| `memory_fts` | FTS5 virtual table indexing Content and Tags |
| `EloRecords` | ContestantId, Rating, Wins, Losses, Draws, LastUpdated |
| `EloHistory` | ContestantId, Rating, UpdatedAt |
| `StrategyRecords` | ContestantId, TacticName, UsedInRound, SessionId, RoundWon |
| `OpinionDriftRecords` | SessionId, Topic, BotName, Model, Persona, InitialStance, FinalStance, StanceDelta, SessionDate |

---

## Dependencies

| Package | Version | Purpose |
|---|---|---|
| `AWSSDK.BedrockRuntime` | 4.0.16 | AWS Bedrock Converse API and streaming |
| `AWSSDK.S3` | 4.0.18.6 | S3 memory store backend |
| `Microsoft.Data.Sqlite` | 9.0.4 | SQLite memory store + FTS5 |
| `Newtonsoft.Json` | 13.0.3 | JSON serialization |
| `Polly` | 8.5.2 | Retry policies and circuit breaker |
| `Spectre.Console` | 0.49.1 | Rich TUI rendering |
| `YamlDotNet` | 16.3.0 | YAML configuration parsing |

OpenRouter uses the standard .NET `HttpClient` --- no extra package needed.

---

## Building & Testing

```bash
# Build
dotnet build

# Run tests (330 tests)
cd ../VibeWars.Tests
dotnet test
```

The test suite covers: SQLite memory operations (FTS5 search, LIKE fallback, wildcard escaping, embeddings, deduplication), S3 store operations (mocked), Hybrid store delegation, Persona resolution, Stance metering and IPS calculation, Fact-check parsing, Tournament bracket generation, Report generation (HTML/Markdown/Podcast), ELO rating math, Audience simulation, Commentary styles, Opinion drift velocity, Challenge detection, Complexity prompt generation, Webhook payloads (Discord/Slack/Teams/Generic), Follow-up topic parsing, Vulnerability tracking, Argument graph parsing, and more.

---

## Supported Bedrock Models

Any model supporting the [Converse API](https://docs.aws.amazon.com/bedrock/latest/userguide/conversation-inference-supported-models-features.html):

- `amazon.nova-micro-v1:0` (fastest & cheapest)
- `amazon.nova-lite-v1:0` (default)
- `amazon.nova-pro-v1:0`
- `anthropic.claude-3-5-haiku-20241022-v1:0`
- `anthropic.claude-3-5-sonnet-20241022-v2:0`
- `meta.llama3-70b-instruct-v1:0`

---

## Examples

```bash
# Simple debate
dotnet run -- "Should AI replace human developers?"

# Interactive topic prompt
dotnet run

# Full analysis suite
dotnet run -- \
  --format structured --complexity academic \
  --persona-a "Domain Expert" --persona-b "Ethicist" \
  --fact-check --argument-graph --stance-tracking \
  --audience --commentator --analytics --reflect --arbiter \
  --elo --post-debate-report \
  "Should nuclear energy be expanded?"

# Red team stress-test
dotnet run -- \
  --red-team \
  --proposal "All new microservices should use serverless" \
  "Cloud architecture decisions"

# Human vs. AI with budget guard
dotnet run -- \
  --human A --think-time 15 \
  --max-cost-usd 0.25 --cost-interactive \
  "The best programming language is Rust"

# Tournament
dotnet run -- tournament "The future of work"

# Batch with parallel execution
dotnet run -- batch topics.txt --parallel 4

# Replay with different models
dotnet run -- replay abc12345 --bot-a-model anthropic/claude-3-5-sonnet --bot-b-persona Contrarian

# Web dashboard
dotnet run -- --web 8080 --audience --commentator "AI and creativity"

# Post results to Slack
dotnet run -- \
  --webhook-url "https://hooks.slack.com/services/..." \
  --webhook-provider slack --webhook-on-complete --webhook-on-round \
  "Remote work vs. office work"

# Check ELO standings
dotnet run -- elo

# View past debate
dotnet run -- memory report abc12345 --format html --out debate.html

# Wave 4: Momentum tracking + highlights + escalating stakes
dotnet run -- --momentum --highlights --stakes escalating "The future of AI"

# Wave 4: Pre-debate hype card with ELO predictions
dotnet run -- --hype "Should AI be regulated?"

# Wave 5: Chain-of-thought planning + knowledge-grounded arguments
dotnet run -- --plan --knowledge wikipedia "Climate change policy"

# Wave 5: Fallacy detection + counter-argument anticipation
dotnet run -- --fallacy-check --lookahead "Is capitalism the best system?"

# Wave 5: Adaptive difficulty balancing
dotnet run -- --balance "The trolley problem"

# Wave 6: Evolving bot personalities + shareable debate cards
dotnet run -- --personality --debate-card "Nuclear energy debate"

# Kitchen sink: every feature active
dotnet run -- \
  --format structured --complexity academic \
  --persona-a "Domain Expert" --persona-b "Ethicist" \
  --fact-check --argument-graph --stance-tracking \
  --audience --commentator --challenges --analytics \
  --reflect --arbiter --strategy --brief \
  --momentum --highlights --hype \
  --plan --knowledge wikipedia --fallacy-check --balance \
  --personality --debate-card --elo \
  --stakes escalating --post-debate-report \
  "Should nuclear energy be expanded?"
```
