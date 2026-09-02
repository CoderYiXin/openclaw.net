# 共享 Harness 状态

共享 Harness 状态是用于委派和多 Agent 工作的结构化状态。它记录谁在参与、每个参与者读取或更改了什么、他们做了哪些假设、什么证据支持他们的工作，以及冲突出现在哪里。

默认情况下它是被动的。它不会启动多 Agent 调度器、改变正常的聊天行为或阻止工具执行。运维人员和未来的治理工作流可以显式创建和检查共享状态。

## 它帮助回答什么

- 哪些 Agent 或运维人员参与了？
- 每个参与者做了什么？
- 他们读取或写入了哪些资源？
- 他们依赖了哪些假设？
- 他们假定了哪些版本依赖？
- 还有哪些验证义务？
- 是否有任何工作流冲突？
- 什么证据支持最终状态？

## 与其他 Harness 原语的关系

- Harness 合约描述计划的工作。
- 证据包记录发生了什么。
- 治理账本记录人类决策。
- 共享 Harness 状态通过相同的活动工作协调多个参与者。
- 计划-执行-验证模式可以为多参与者运行使用共享状态。
- 分形记忆存储持久的项目记忆，而共享 Harness 状态跟踪活动的事务性工作。

## 管理 API

读取端点需要运维人员认证：

```text
GET /admin/harness/shared-state
GET /admin/harness/shared-state/{id}
GET /admin/sessions/{sessionId}/harness-state
```

变更端点需要运维人员认证和针对浏览器会话的 CSRF 保护：

```text
POST /admin/harness/shared-state
POST /admin/harness/shared-state/{id}/participants
POST /admin/harness/shared-state/{id}/actions
POST /admin/harness/shared-state/{id}/detect-conflicts
```

CLI 检查是网关支持的：

```bash
openclaw harness state list
openclaw harness state show shs_example
openclaw harness state session session-123
openclaw harness state conflicts shs_example
openclaw harness state list --session session-123 --json
```

## JSON 示例

```json
{
  "id": "shs_release_docs",
  "sessionId": "session-release",
  "parentSessionId": "session-manager",
  "harnessContractId": "hctr_release",
  "status": "active",
  "goal": "Prepare release notes with independent review",
  "participants": [
    {
      "id": "manager",
      "role": "manager",
      "sessionId": "session-manager",
      "displayName": "Release manager"
    },
    {
      "id": "docs",
      "role": "docs_writer",
      "sessionId": "session-docs"
    }
  ],
  "actions": [
    {
      "id": "draft-release-notes",
      "participantId": "docs",
      "title": "Draft release notes",
      "status": "active",
      "toolName": "file_write",
      "readSet": [
        { "kind": "file", "path": "CHANGELOG.md", "version": "main@abc123" }
      ],
      "writeSet": [
        { "kind": "file", "path": "docs/RELEASES.md" }
      ],
      "versionDependencies": [
        {
          "id": "changelog-version",
          "resource": { "kind": "file", "path": "CHANGELOG.md" },
          "version": "main@abc123"
        }
      ],
      "verifierObligations": [
        {
          "id": "review-docs",
          "title": "Review release notes",
          "verifier": "reviewer",
          "required": true
        }
      ]
    }
  ],
  "evidenceBundleIds": ["evb_release_docs"],
  "tags": ["release", "docs"]
}
```

## 冲突检测

初始检测器记录冲突和建议。它不会阻止执行。

它检测：

- 当两个操作写入相同资源的写/写冲突
- 当版本依赖的读取与另一个操作的写入重叠时的读/写冲突
- 当相同假设键具有不同值时的假设冲突
- 高风险写入操作缺少验证器义务

冲突策略默认值是保守的：

- 中或更低风险的冲突使用 `warn`
- 高或关键风险冲突使用 `escalate`

## 目前不做的事情

- 不是完整的语义冲突解决。
- 不是强制的多 Agent 编排。
- 不会自动合并或修复冲突的工作。
- 默认不会阻止执行。
- 不是人类审查的替代品。
- 尚未自动捕获每个委派的工具调用；使用 API 或未来的 PEV/委派集成进行显式捕获。
