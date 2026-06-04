import React from "react";
import { useTranslation } from "react-i18next";

type Role = "viewer" | "admin";

interface Command {
  key: string;
  syntax: string;
  aliases?: string;
  example?: string;
  role: Role;
}

const COMMANDS: Command[] = [
  // ── Viewer ──────────────────────────────────────────────────────────────────
  {
    key: "play",
    role: "viewer",
    syntax: "!play <canción>",
    aliases: "!sr",
    example: "!play Bad Bunny Tití me preguntó",
  },
  {
    key: "revoke",
    role: "viewer",
    syntax: "!revoke",
    aliases: "!quitar",
  },
  {
    key: "skip",
    role: "viewer",
    syntax: "!skip",
  },
  {
    key: "bump",
    role: "viewer",
    syntax: "!bump",
  },
  {
    key: "pos",
    role: "viewer",
    syntax: "!pos",
    aliases: "!position",
  },
  {
    key: "info",
    role: "viewer",
    syntax: "!info",
  },
  {
    key: "song",
    role: "viewer",
    syntax: "!song",
    aliases: "!cancion · !current",
  },
  {
    key: "queue",
    role: "viewer",
    syntax: "!queue",
    aliases: "!cola",
  },
  {
    key: "history",
    role: "viewer",
    syntax: "!history",
    aliases: "!historial",
  },
  {
    key: "like",
    role: "viewer",
    syntax: "!like",
    aliases: "!love",
  },
  {
    key: "here",
    role: "viewer",
    syntax: "!aqui",
    aliases: "!here",
  },
  {
    key: "vote",
    role: "viewer",
    syntax: "!si · !yes / !no",
  },
  {
    key: "keep",
    role: "viewer",
    syntax: "!keep",
  },
];

const RoleBadge: React.FC<{ role: Role }> = ({ role }) => {
  const { t } = useTranslation();
  return (
    <span className={`cmd-role-badge cmd-role-${role}`}>
      {role === "admin" ? t("commands.roleAdmin") : t("commands.roleViewer")}
    </span>
  );
};

export const CommandsPanel: React.FC = () => {
  const { t } = useTranslation();
  return (
    <div className="commands-panel">
      <div className="commands-panel-header">
        <p className="commands-panel-hint">
          {t("commands.prefixHint")} <code>!</code>, <code>.</code> {t("commands.prefixHintAnd")} <code>/</code>
          &nbsp;{t("commands.prefixHintEg")} <code>!play</code>, <code>.play</code> {t("commands.prefixHintOr")} <code>/play</code>.
        </p>
      </div>
      <table className="commands-table">
        <thead>
          <tr>
            <th>{t("commands.thCommand")}</th>
            <th>{t("commands.thAlias")}</th>
            <th>{t("commands.thRole")}</th>
            <th>{t("commands.thDesc")}</th>
          </tr>
        </thead>
        <tbody>
          {COMMANDS.map((cmd, i) => (
            <tr key={i}>
              <td><code>{cmd.syntax}</code></td>
              <td>{cmd.aliases ? <code>{cmd.aliases}</code> : <span className="commands-none">—</span>}</td>
              <td><RoleBadge role={cmd.role} /></td>
              <td>
                {t(`commands.${cmd.key}.desc`)}
                {cmd.example && (
                  <span className="commands-example"> {t("commands.exPrefix")} <code>{cmd.example}</code></span>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
};
