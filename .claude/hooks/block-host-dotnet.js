// PreToolUse(Bash): block dotnet / dotnet-ef / psql run on the host instead of inside the compose containers.
// The host does have a .NET SDK installed (used by other tools), so a host run would not fail;
// it would silently connect to the wrong DB or write bin/obj into the bind-mounted source tree.
const BLOCKED_COMMANDS = new Set(["dotnet", "dotnet-ef", "psql"]);
// Read-only version queries are harmless and useful for diagnostics.
const ALLOWED_ARGUMENTS = new Set(["--version", "--info", "--list-sdks", "--list-runtimes"]);

// Quoted strings are arguments (`sh -c "dotnet test"` inside docker exec, `echo "dotnet"`), not commands,
// except a quoted executable path such as "/c/Program Files/dotnet/dotnet".
function maskQuotedArguments(command) {
  return command.replace(/"[^"]*"|'[^']*'/g, (quoted) => {
    const content = quoted.slice(1, -1);
    return /[\\/](dotnet|dotnet-ef|psql)(\.exe)?$/i.test(content) ? content.replace(/\s/g, "_") : "QUOTED";
  });
}

function findHostInvocation(command) {
  const segments = maskQuotedArguments(command).split(/&&|\|\||[;|\n]/);
  for (const segment of segments) {
    // Skip leading env assignments such as `FOO=bar dotnet test`.
    const tokens = segment.trim().split(/\s+/).filter((token) => !/^\w+=/.test(token));
    const executable = (tokens[0] || "").replace(/^.*[\\/]/, "").replace(/\.exe$/i, "");
    if (BLOCKED_COMMANDS.has(executable) && !ALLOWED_ARGUMENTS.has(tokens[1])) return segment.trim();
  }
  return null;
}

let raw = "";
process.stdin.on("data", (chunk) => (raw += chunk));
process.stdin.on("end", () => {
  const input = JSON.parse(raw);
  const invocation = findHostInvocation((input.tool_input && input.tool_input.command) || "");
  if (!invocation) process.exit(0);

  process.stderr.write(
    `已攔截本機執行：\`${invocation}\`\n` +
      "本專案的 dotnet / dotnet-ef / psql 一律在容器內執行，例如 `docker compose exec api dotnet test`（見 CLAUDE.md「執行環境」）。\n"
  );
  // Exit code 2 blocks the tool call and feeds stderr back to Claude.
  process.exit(2);
});
