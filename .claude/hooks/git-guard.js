#!/usr/bin/env node
// Hook PreToolUse (Bash/PowerShell): bloqueia comandos git destrutivos ou que reescrevem histórico.
// Sai com código 2 e explica o motivo; o Claude Code então cancela a chamada.
// Para executar um desses comandos de propósito, rode-o você mesmo no terminal.

let entrada = "";
process.stdin.on("data", (c) => (entrada += c));
process.stdin.on("end", () => {
  let comando = "";
  try {
    comando = JSON.parse(entrada)?.tool_input?.command ?? "";
  } catch {
    process.exit(0);
  }

  const regras = [
    [/\bgit\b[^;&|\n]*\bpush\b[^;&|\n]*(\s--force(?!-with-lease)\b|\s-[a-zA-Z]*f\b)/, "git push --force (use --force-with-lease, se for mesmo necessário)"],
    [/\bgit\b[^;&|\n]*\bpush\b[^;&|\n]*(\s--delete\b|\s-d\b|\s:[\w./-]+)/, "git push apagando branch remoto"],
    [/\bgit\b[^;&|\n]*\breset\b[^;&|\n]*--hard\b/, "git reset --hard"],
    [/\bgit\b[^;&|\n]*\bclean\b[^;&|\n]*\s-[a-zA-Z]*f/, "git clean -f"],
    [/\bgit\b[^;&|\n]*\bcheckout\b[^;&|\n]*(\s--\s|\s\.(\s|$))/, "git checkout -- <arquivos> (descarta alterações locais)"],
    [/\bgit\b[^;&|\n]*\brestore\b(?![^;&|\n]*--staged)/, "git restore (descarta alterações locais)"],
    [/\bgit\b[^;&|\n]*\bbranch\b[^;&|\n]*(\s-D\b|\s--delete\s+--force\b)/, "git branch -D"],
    [/\bgit\b[^;&|\n]*\b(filter-branch|filter-repo)\b/, "reescrita de histórico"],
    [/\bgit\b[^;&|\n]*\bstash\b\s+(drop|clear)\b/, "git stash drop/clear"],
    [/\bgit\b[^;&|\n]*\breflog\b\s+(expire|delete)\b/, "git reflog expire/delete"],
    [/\bgit\b[^;&|\n]*\bgc\b[^;&|\n]*--prune=now\b/, "git gc --prune=now"],
    [/\brm\b[^;&|\n]*\s-[a-zA-Z]*[rR][a-zA-Z]*\s+[^;&|\n]*\.git(\s|\/|$)/, "apagar a pasta .git"],
    [/\bRemove-Item\b[^;&|\n]*\.git(\s|\\|\/|'|"|$)[^;&|\n]*-Recurse|\bRemove-Item\b[^;&|\n]*-Recurse[^;&|\n]*\.git(\s|\\|\/|'|"|$)/i, "apagar a pasta .git"],
  ];

  for (const [padrao, nome] of regras) {
    if (padrao.test(comando)) {
      process.stderr.write(
        `Bloqueado pelo git-guard: ${nome}.\n` +
          "Esse comando pode apagar trabalho ou reescrever o histórico. " +
          "Se for intencional, peça ao usuário para executá-lo no terminal.\n"
      );
      process.exit(2);
    }
  }
  process.exit(0);
});
