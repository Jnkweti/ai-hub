# AI Hub collaboration

This app-bundled plugin provides three shared workflows for Codex and Claude Code. AI Hub loads these exact skill files into each structured dispatch's instructions, so both providers receive the same workflow without changing global plugin configuration. The manifests also allow independent skill discovery when installed in a compatible provider plugin manager.

The app supplies the `ai_hub` MCP server for each dispatch. Its named pipe and credential are short lived and bound to the task, provider, session, and generation. There is deliberately no persistent MCP endpoint in the plugin manifests. Running the skills outside AI Hub requires an active AI Hub connection; the skill files alone cannot start or authorize work.

Main tools: `get_task_context`, `get_shared_context`, `get_context_records`, `read_context_record`, `read_context_source`, `get_messages`, `get_evidence`, `submit_message`, `mark_addressed`. A `context_request` divides research into separate areas. Temporary read-only researchers receive task/shared/original-context retrieval and `publish_context`. Execution stays in the provider's native tools and permission limits. Shared findings persist with the task and include scoped file freshness.

Version 0.9 automatically supplies a common task snapshot plus each worker's assignment. The host saves exact input manifests before dispatch. New user phases use fresh native sessions; active user instructions and attributed findings come from durable originals. Only the user-facing desktop can pin or supersede instructions. Tool output, agent agreement, and a shared context version cannot create user authority or certify correctness.

Packaging follows [Codex plugin examples](https://github.com/openai/plugins) and [Claude plugin documentation](https://code.claude.com/docs/en/plugins). No marketplace or global installation is needed for the bundled desktop workflows.

Version 0.10 adds concurrent preparation with ordered speaking, and task-scoped `claim_work`, `complete_work`, and `get_work` tools. These tools coordinate cooperating agents; arbitrary native tool calls outside the protocol are not deduplicated. Shared check reuse requires matching recorded inputs and native command evidence. Independent review keeps its fresh-evidence requirement. The host supplies research directly and schedules one synthesis after a split.
