# OpenClaw SkillKit

OpenClaw SkillKit 是一个本地优先、基于文件的技能编写工作流，用于构建可复用的 OpenClaw 技能。它帮助用户定义清晰的意图、所需输入、输出期望、工具策略、防护栏、人工审批点和验证检查，然后 Agent 才执行工作。

SkillKit 是本地优先和基于文件的。它不是托管的技能市场或治理仪表板。目标是帮助 OpenClaw.NET 用户定义具有清晰意图、工具策略、防护栏、人工审批点和验证检查的可复用 Agent 技能。

## 它解决什么问题

SkillKit 将粗略的人类目标转化为可检查的技能包。它对开发者和非开发者任务都很有用，例如会议摘要、捐赠者提案草案、合规审查、政务服务请求分类、研究洞察提取、实施计划和 Codex 构建提示。

第一个版本是 CLI 优先和确定性的。在技能创建、评审、验证、打包或干运行规划期间，它不会调用 LLM。

## 包结构

默认情况下，生成的技能位于 `.openclaw/skills/{skill-id}/` 下。

```text
.openclaw/skills/{skill-id}/
  skill.yaml
  intent.md
  expectations.md
  workflow.yaml
  tools.yaml
  guardrails.md
  validation.md
  examples.md
  trace.md
```

`skill.yaml` 是规范的机器可读清单。Markdown 和支持 YAML 文件使技能易于人工审查和编辑。

## CLI 命令

```bash
openclaw skill new "Community Research Insight Extractor" --category research
openclaw skill list
openclaw skill validate community.research_insight
openclaw skill critique community.research_insight
openclaw skill generate community.research_insight
openclaw skill package community.research_insight
openclaw skill run community.research_insight --input transcript.md --dry-run
```

使用 `--output <path>` 选择技能根目录。默认是 `.openclaw/skills`。包写入 `.openclaw/packages`，除非提供 `--package-output <path>`。

## 创建研究技能

```bash
openclaw skill new "Community Research Insight Extractor" --category research --template research
openclaw skill validate community.research_insight
openclaw skill critique community.research_insight
```

研究模板包括扎实的输出期望、社区参与研究的防护栏、禁止的外部操作以及最终建议、外部发布和具名归属的人工审查点。

## 创建提案技能

```bash
openclaw skill new "Donor Proposal Concept Note Builder" --category proposal --template proposal
openclaw skill run donor.proposal_concept_note_builder --input project-brief.md --dry-run
```

提案模板侧重于概念摘要、需求陈述、拟议活动、成果、风险和审查问题。

## 验证规则

`openclaw skill validate` 报告通过、警告和失败检查。错误返回非零退出码；警告不返回。

验证检查包括：

- 必需的包文件存在
- 清单 ID 匹配文件夹名称或声明的别名
- name、version、category 和 intent outcome 存在
- 定义了必需的输入和输出
- 允许和禁止的工具不重叠
- 需要审批的工具不被禁止
- 工作流至少有一个步骤
- 定义了验证检查和防护栏
- trace 文件存在

## 干运行执行

`openclaw skill run` 在此 MVP 中是一个干运行规划器。它读取技能，验证输入文件存在，打印工作流步骤，并显示工具和审批策略。它不执行工具、调用模型或修改文件。

一旦 SkillKit 包连接到治理的 OpenClaw 运行时流程，可以在以后添加完整的运行时执行。

## 当前限制

- 没有 LLM 驱动的技能生成或评审
- 没有 Web 仪表板
- 没有托管市场
- 没有数据库支持的注册表
- 没有自主技能执行
- 没有分布式执行
- YAML 解析支持 SkillKit 生成的子集，而非任意 YAML

## 未来方向

SkillKit 在 OpenClaw.NET 中故意保持轻量级。它可以通过将技能包链接到 Harness 合约、证据包、治理账本条、Harness 回归检查和计划-执行-验证运行，从而向 AgentQi 风格的治理技能工作流发展。
