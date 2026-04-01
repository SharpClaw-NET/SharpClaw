Sandboxed command execution via mk8.shell. No real shell, eval, pipes, chaining, or expansion. Arguments are structured arrays.
Script: {"operations":[{"verb":"...","args":["..."],"workingDirectory":"$WORKSPACE/subdir"}],"options":{...},"cleanup":[...]}
Variables: $WORKSPACE (sandbox root), $CWD, $USER, $PREV (when pipeStepOutput:true). Only ProcRun spawns processes (strict template whitelist).
Run introspection verbs to discover capabilities at runtime: Mk8Docs (full reference), Mk8Verbs (all verbs), Mk8Templates (ProcRun whitelist).
You cannot create/manage sandboxes — tell user to register if missing.
