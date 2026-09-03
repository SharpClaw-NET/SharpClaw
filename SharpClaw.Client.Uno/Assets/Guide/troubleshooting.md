<![CDATA[# Troubleshooting

## Runtime Is Not Reachable

Check that the local Runtime process is running and that its loopback address matches the configured client address. Do not use a public Gateway address for local Runtime access.

If the Runtime does not start, inspect its process output for configuration, protected environment, database, or module errors. A configured remote mode must fail closed when its connection or pairing is invalid.

## A Provider Request Fails

Check that the provider module is enabled and that the selected model belongs to that provider. Check the Runtime provider endpoint and credential.

The Runtime does not replace a disabled or invalid provider with a hidden fallback. Correct the provider configuration and retry the request.

## A Job Does Not Progress

Check Job state through the Runtime API. A queued Job requires an active Job worker. A failed Job exposes its declared failure state.

Restart the Runtime only after recording the Job identity and state. The canonical Jobs path must recover durable state after restart.

## The Gateway Is Not Available

The Gateway is optional. Local Runtime use does not require it. If another process needs the Gateway, check its loopback listener, process output, and API key configuration.

The public Gateway pipeline is separate from local Runtime operation. A Gateway failure must not create a second local execution path.
]]>
