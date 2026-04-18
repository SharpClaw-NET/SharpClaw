<![CDATA[# Permissions & Roles

Control what agents and users can do with granular permissions and roles.

## Permission System Overview

SharpClaw uses a two-level permission system:

1. **Agent Capability Check**: Does the agent's role grant this permission?
2. **Channel/Context Pre-Authorization**: Is the action pre-approved in this context?

### Permission Resolution Flow

When an agent attempts an action:

1. **Agent role** is checked first
   - If **Independent** clearance → auto-approved
   - If denied → action denied immediately
   - If **PendingApproval** → proceed to step 2

2. **Channel permission set** is checked
   - If it addresses the action → that result is final
   - If not → **context permission set** is checked

3. Final clearance:
   - **Independent** → pre-authorized (job runs immediately)
   - **PendingApproval** → awaits user approval
   - **Denied** → action blocked

## Clearance Levels

Every permission grant has a **clearance** level:

- **Independent**: Action is pre-approved; no user interaction needed
- **PendingApproval**: Action requires explicit user approval before execution
- **Denied**: Action is blocked entirely

## Roles

A **role** is a named permission set that can be assigned to agents and users.

### Creating a Role

1. Go to **Settings** → **Roles**
2. Click **+** and enter a name
3. Click **Create**

### Editing Role Permissions

1. Click the role's name in the list
2. Configure **Global Permissions** (flags) and **Resource Accesses** (per-resource grants)
3. Click **Save Permissions**

### Assigning Roles

**To an agent**:

1. Go to **Settings** → **Agents**
2. Click the agent's name
3. Select a role from the dropdown
4. Click **Assign Role**

**To a user** (admin only):

1. Go to **Settings** → **Users**
2. Select a role for the user
3. Click **Assign**

### Cloning Roles

Click the **⎘** icon next to a role to duplicate it. Useful for creating variations.

## Global Permissions (Flags)

Global flags grant broad capabilities:

- **CanCreateSubAgents**: Agent can create new agents
- **CanCreateContainers**: Agent can provision Docker-like containers
- **CanRegisterInfoStores**: Agent can register new information stores
- **CanAccessLocalhostInBrowser**: Agent can open localhost URLs in a headless browser
- **CanAccessLocalhostCli**: Agent can make HTTP requests to localhost
- **CanClickDesktop**: Agent can simulate mouse clicks on the desktop
- **CanTypeOnDesktop**: Agent can simulate keyboard input on the desktop
- **CanReadCrossThreadHistory**: Agent can read conversation history from other channels
- **CanEditAgentHeader**: Agent can modify custom chat headers for agents
- **CanEditChannelHeader**: Agent can modify custom chat headers for channels

Each flag has its own clearance level.

## Resource Accesses (Per-Resource Grants)

Resource permissions are tied to specific instances:

- **Dangerous Shell Accesses**: Execute unrestricted shell commands via mk8.shell
- **Safe Shell Accesses**: Execute sandboxed mk8.shell commands
- **Container Accesses**: Access specific Docker-like containers
- **Website Accesses**: Browse specific websites
- **Search Engine Accesses**: Query specific search engines
- **Local Info Store Accesses**: Query local information stores
- **External Info Store Accesses**: Query external APIs
- **Input Audio Accesses** (`TrAudio`): Use specific audio input devices
- **Display Device Accesses**: Capture screenshots from specific displays
- **Agent Management Accesses**: Modify specific agents
- **Task Manage Accesses**: Edit specific task definitions
- **Skill Manage Accesses**: Modify specific skills
- **Agent Header Accesses**: Edit chat headers for specific agents
- **Channel Header Accesses**: Edit chat headers for specific channels

### Granting Resource Access

1. In the role editor, scroll to **Resource Accesses**
2. Click **+ Add** for the access type
3. Select a resource from the dropdown (or use **All Resources** wildcard)
4. Choose a clearance level
5. Click **Save Permissions**

### Wildcard Grants

The **All Resources** wildcard (`ffffffff-ffff-ffff-ffff-ffffffffffff`) grants access to all resources of that type.

**Warning**: Wildcard grants are powerful — use them carefully.

## Channel & Context Permission Overrides

Channels and contexts can override agent permissions:

1. Open channel (or context) settings
2. Go to **Permissions** tab
3. Configure overrides
4. Save

This is useful for:

- **Restricting** an otherwise powerful agent in a specific channel
- **Granting** additional permissions for a specific project
- **Pre-authorizing** actions that normally require approval

## Default Clearance

Roles have a **default clearance** that applies when:

- An action is requested
- The agent role doesn't explicitly grant/deny it
- The channel/context doesn't override it

**Options**:

- **Independent**: Allow by default
- **PendingApproval**: Require approval by default
- **Denied**: Block by default

**Use case**: Set to **Denied** for restrictive roles, **Independent** for trusted agents.

## Permission Filtering

When editing role permissions, you can only grant permissions you hold yourself.

**Example**: If you don't have **CanCreateSubAgents**, you can't grant it to a role.

This prevents privilege escalation.

## Cross-Thread History Access

For agents to read history from other channels:

1. Agent role must have **CanReadCrossThreadHistory** = true
2. Target channel must also have **CanReadCrossThreadHistory** = true (opt-in)
3. Agent must be primary or in allowed agents list for target channel
4. **Independent** clearance on agent role overrides channel opt-in

## Container Ownership

When a container is created, a role is auto-created:

- Name: `[ContainerName] Owner`
- Permissions: **Independent** clearance for:
  - ContainerAccesses (the new container)
  - SafeShellAccesses (all safe shell resources)

This role is assigned to the creating user if they have no role.

## Best Practices

- **Start restrictive**: Use **PendingApproval** or **Denied** by default, grant **Independent** only to trusted actions
- **Use roles for groups**: Create roles like "Developer", "Researcher", "Admin" rather than per-agent roles
- **Leverage channel overrides**: Fine-tune permissions per project without modifying agent roles
- **Audit grants regularly**: Review what agents can do, especially with wildcard grants
- **Test with PendingApproval first**: Before granting **Independent**, test actions manually

## Common Permission Setups

### Safe Research Agent

- **CanAccessLocalhostCli**: Independent (can query localhost APIs)
- **SafeShellAccesses**: Independent for whitelisted commands
- **WebsiteAccesses**: PendingApproval for external sites
- Default clearance: **Denied**

### Full Developer Agent

- **CanCreateContainers**: Independent
- **DangerousShellAccesses**: Independent for all resources (wildcard)
- **CanClickDesktop, CanTypeOnDesktop**: Independent
- **CanReadCrossThreadHistory**: Independent
- Default clearance: **Independent**

### Restricted Assistant

- **SafeShellAccesses**: PendingApproval for whitelisted commands only
- All other permissions: **Denied**
- Default clearance: **Denied**

## Next Steps

Continue to **Jobs & Tasks** to learn how agents execute background actions.
]]>
