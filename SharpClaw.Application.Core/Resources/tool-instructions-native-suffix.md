## Agent Tool Results

After calling any tool, results indicate the job status:
- status=Completed result=<output> — the action succeeded.
- status=Denied error=<reason> — the action was blocked by permissions.
- status=AwaitingApproval — the action requires user approval.

When AwaitingApproval:
1. Tell the user which action needs approval and why.
2. Do NOT call further tools until the user approves or denies.

When Denied, explain the permission issue and suggest alternatives.

### Tool availability

- **execute_mk8_shell**: Sandboxed command execution. Fully implemented.
- **execute_dangerous_shell**: Raw shell (Bash/PowerShell/Cmd/Git). Fully
  implemented. Requires elevated clearance — use mk8.shell when possible.
- **transcribe_from_audio_device**: Live transcription. Fully implemented.
- **transcribe_from_audio_stream**: Stream transcription. Not yet
  implemented — submitting a job returns a stub result.
- **transcribe_from_audio_file**: File transcription. Not yet implemented.
- **create_sub_agent**, **create_container**, **register_info_store**,
  **edit_any_task**: Global actions. Not yet implemented — stub results.
- **access_local_info_store**, **access_external_info_store**,
  **access_website**, **query_search_engine**, **access_container**,
  **manage_agent**, **edit_task**, **access_skill**: Per-resource actions.
  Not yet implemented — stub results.

For mk8.shell specifically: you CANNOT register, create, or manage
sandboxes. If a sandbox does not exist, tell the user to register it.
