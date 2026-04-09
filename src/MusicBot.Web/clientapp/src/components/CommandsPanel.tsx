import React from "react";

type Role = "viewer" | "admin";

interface Command {
  syntax: string;
  aliases?: string;
  description: string;
  example?: string;
  role: Role;
}

const COMMANDS: Command[] = [
  // ── Viewer ──────────────────────────────────────────────────────────────────
  {
    role: "viewer",
    syntax: "!play <canción>",
    aliases: "!sr",
    description: "Solicita una canción por nombre, artista o URL de YouTube.",
    example: "!play Bad Bunny Tití me preguntó",
  },
  {
    role: "viewer",
    syntax: "!revoke",
    aliases: "!quitar",
    description: "Elimina tu propia canción de la cola antes de que se reproduzca.",
  },
  {
    role: "viewer",
    syntax: "!skip",
    description: "Salta tu canción si está sonando en este momento (solo la tuya).",
  },
  {
    role: "viewer",
    syntax: "!bump",
    description: "Sube tu canción una posición en la cola.",
  },
  {
    role: "viewer",
    syntax: "!pos",
    aliases: "!position",
    description: "Muestra en qué posición está tu canción y el tiempo estimado de espera.",
  },
  {
    role: "viewer",
    syntax: "!info",
    description: "Muestra cuántas canciones tienes en la cola y en qué posiciones.",
  },
  {
    role: "viewer",
    syntax: "!song",
    aliases: "!cancion · !current",
    description: "Muestra la canción que está sonando en este momento.",
  },
  {
    role: "viewer",
    syntax: "!queue",
    aliases: "!cola",
    description: "Muestra las próximas canciones en la cola de solicitudes.",
  },
  {
    role: "viewer",
    syntax: "!history",
    aliases: "!historial",
    description: "Muestra las últimas 3 canciones reproducidas.",
  },
  {
    role: "viewer",
    syntax: "!like",
    aliases: "!love",
    description: "Guarda la canción actual en la auto-cola para que vuelva a sonar.",
  },
  {
    role: "viewer",
    syntax: "!aqui",
    aliases: "!here",
    description: "Confirma tu presencia cuando el bot te avisa que tu canción está por sonar.",
  },
  {
    role: "viewer",
    syntax: "!si · !yes / !no",
    description: "Vota para saltar (o no) la canción actual durante una votación.",
  },
  {
    role: "viewer",
    syntax: "!keep",
    description: "Salva la canción actual de ser eliminada durante un voto de skip.",
  },
];

const RoleBadge: React.FC<{ role: Role }> = ({ role }) => (
  <span className={`cmd-role-badge cmd-role-${role}`}>
    {role === "admin" ? "Admin" : "Viewer"}
  </span>
);

export const CommandsPanel: React.FC = () => (
  <div className="commands-panel">
    <div className="commands-panel-header">
      <p className="commands-panel-hint">
        Todos los comandos funcionan con los prefijos <code>!</code>, <code>.</code> y <code>/</code>
        &nbsp;— por ejemplo <code>!play</code>, <code>.play</code> o <code>/play</code>.
      </p>
    </div>
    <table className="commands-table">
      <thead>
        <tr>
          <th>Comando</th>
          <th>Alias</th>
          <th>Rol</th>
          <th>Descripción</th>
        </tr>
      </thead>
      <tbody>
        {COMMANDS.map((cmd, i) => (
          <tr key={i}>
            <td><code>{cmd.syntax}</code></td>
            <td>{cmd.aliases ? <code>{cmd.aliases}</code> : <span className="commands-none">—</span>}</td>
            <td><RoleBadge role={cmd.role} /></td>
            <td>
              {cmd.description}
              {cmd.example && (
                <span className="commands-example"> Ej: <code>{cmd.example}</code></span>
              )}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  </div>
);
