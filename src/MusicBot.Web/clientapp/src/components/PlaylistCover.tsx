import React from "react";
import { Music } from "lucide-react";

interface Props {
  coverUrls?: string[];
  /** Icon size when no covers are available */
  iconSize?: number;
  className?: string;
}

/**
 * Renders a 2×2 mosaic when 4+ covers exist, a single cover for 1-3,
 * or a music icon when the playlist has no covers yet.
 */
export const PlaylistCover: React.FC<Props> = ({ coverUrls = [], iconSize = 28, className }) => {
  const filled = coverUrls.filter(Boolean);

  if (filled.length === 0) {
    return <Music size={iconSize} />;
  }

  if (filled.length < 4) {
    return (
      <img
        src={filled[0]}
        alt=""
        className={`pl-cover-single${className ? ` ${className}` : ""}`}
      />
    );
  }

  return (
    <div className={`pl-cover-mosaic${className ? ` ${className}` : ""}`}>
      {filled.slice(0, 4).map((url, i) => (
        <img key={i} src={url} alt="" />
      ))}
    </div>
  );
};
