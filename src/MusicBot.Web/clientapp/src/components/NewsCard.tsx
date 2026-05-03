import React from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import rehypeRaw from "rehype-raw";
import { ExternalLink, Trash2, Pencil } from "lucide-react";
import { NewsItem } from "../services/community/ICommunityService";
import { api } from "../services/api";

const TAG_LABELS: Record<NewsItem["tag"], string> = {
  novedad:   "Novedad",
  mejora:    "Mejora",
  arreglado: "Arreglado",
};

// Open external links in system browser instead of WebView2
function handleExternalLink(e: React.MouseEvent<HTMLAnchorElement>) {
  const href = e.currentTarget.href;
  if (href && !href.startsWith(window.location.origin)) {
    e.preventDefault();
    api.openInBrowser(href);
  }
}

// Custom renderers for markdown elements
const markdownComponents: React.ComponentProps<typeof ReactMarkdown>["components"] = {
  a: ({ href, children, ...props }) => (
    <a
      {...props}
      href={href}
      onClick={handleExternalLink}
      rel="noreferrer"
      className="news-md-link"
    >
      {children}
    </a>
  ),
  img: ({ src, alt, ...props }) => (
    <span className="news-md-img-wrap">
      <img {...props} src={src} alt={alt ?? ""} className="news-md-img" loading="lazy" />
      {alt && <span className="news-md-img-caption">{alt}</span>}
    </span>
  ),
  iframe: ({ ...props }) => (
    <span className="news-md-video-wrap">
      <iframe {...props} className="news-md-iframe" allowFullScreen />
    </span>
  ),
  h1: ({ children }) => <h1 className="news-md-h1">{children}</h1>,
  h2: ({ children }) => <h2 className="news-md-h2">{children}</h2>,
  h3: ({ children }) => <h3 className="news-md-h3">{children}</h3>,
  code: ({ children, className }) => {
    const isBlock = className?.startsWith("language-");
    return isBlock
      ? <code className={`news-md-code-block ${className ?? ""}`}>{children}</code>
      : <code className="news-md-code-inline">{children}</code>;
  },
  pre: ({ children }) => <pre className="news-md-pre">{children}</pre>,
  blockquote: ({ children }) => <blockquote className="news-md-blockquote">{children}</blockquote>,
  ul: ({ children }) => <ul className="news-md-ul">{children}</ul>,
  ol: ({ children }) => <ol className="news-md-ol">{children}</ol>,
  hr: () => <hr className="news-md-hr" />,
};

interface Props {
  item: NewsItem;
  onDelete?: () => void;
  onEdit?: () => void;
}

export const NewsCard: React.FC<Props> = ({ item, onDelete, onEdit }) => {
  const hasBody = !!item.body?.trim();

  const formattedDate = new Date(item.date).toLocaleDateString("es-ES", {
    day: "numeric", month: "long", year: "numeric",
  });

  return (
    <article className="news-card-v2">
      <div className="news-card-v2-header">
        <div className="news-card-v2-meta">
          <span className={`news-tag news-tag-${item.tag}`}>{TAG_LABELS[item.tag]}</span>
          <span className="news-card-v2-date">{formattedDate}</span>
          {onEdit && (
            <button className="news-admin-edit-btn" onClick={onEdit} title="Editar novedad">
              <Pencil size={13} />
            </button>
          )}
          {onDelete && (
            <button className="news-admin-delete-btn" onClick={onDelete} title="Eliminar novedad">
              <Trash2 size={13} />
            </button>
          )}
        </div>
        <h3 className="news-card-v2-title">{item.title}</h3>
        <p className="news-card-v2-excerpt">{item.excerpt}</p>
      </div>

      {hasBody && (
        <div className="news-card-v2-body">
          <ReactMarkdown
            remarkPlugins={[remarkGfm]}
            rehypePlugins={[rehypeRaw]}
            components={markdownComponents}
          >
            {item.body!}
          </ReactMarkdown>
        </div>
      )}

      {!hasBody && item.url && (
        <button className="news-card-v2-toggle" onClick={() => api.openInBrowser(item.url!)}>
          <ExternalLink size={14} /> Leer más
        </button>
      )}
    </article>
  );
};
