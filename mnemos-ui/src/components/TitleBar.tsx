// ---------------------------------------------------------------
// TitleBar — Custom window title bar with drag support, menu
// toggle, and window controls (minimize, maximize, close).
// Communicates with the WPF host via Chrome WebView postMessage.
// ---------------------------------------------------------------

import { useState } from "react";

interface Props {
    /** Callback when the hamburger menu button is clicked. */
    onMenuClick?: () => void;
    /** Whether to show the menu toggle button. */
    showMenu?: boolean;
    /** Whether the dark theme is active. */
    isDark?: boolean;
}

/**
 * Renders a borderless window title bar with glass-like backdrop blur.
 * The entire bar acts as a drag handle for moving the WPF window.
 */
export default function TitleBar({ onMenuClick, showMenu, isDark = false }: Props) {
    const [isMax, setIsMax] = useState(false);

    /** Sends a command to the WPF host via WebView postMessage. */
    const send = (m: string) => window.chrome?.webview?.postMessage(m);

    const theme = {
        bg: isDark
            ? "bg-slate-900/90 border-slate-800"
            : "bg-white/90 border-slate-200",
        text: isDark ? "text-slate-400" : "text-slate-500",
        hoverBtn: isDark ? "hover:bg-slate-800" : "hover:bg-slate-100",
        hoverClose: isDark
            ? "hover:bg-red-500/10 hover:text-red-400"
            : "hover:bg-red-50 hover:text-red-500",
        dotOuter: isDark ? "bg-indigo-500/20" : "bg-indigo-500/20",
        dotInner: isDark ? "bg-indigo-400" : "bg-indigo-500",
        menuBtn: isDark
            ? "hover:bg-slate-800 text-slate-400"
            : "hover:bg-slate-100 text-slate-500",
    };

    return (
        <div
            className={`h-9 backdrop-blur border-b flex items-center justify-between select-none shrink-0 z-50 transition-colors duration-500 ${theme.bg}`}
            onMouseDown={() => send("drag")}
        >
            {/* Left zone — menu toggle (hamburger) */}
            <div className="flex items-center gap-2 px-3 flex-1 h-full cursor-default">
                {showMenu && (
                    <button
                        onClick={onMenuClick}
                        onMouseDown={(e) => e.stopPropagation()}
                        className={`p-1.5 rounded-md transition-colors mr-1 ${theme.menuBtn}`}
                    >
                        <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 6h16M4 12h16M4 18h16" />
                        </svg>
                    </button>
                )}
            </div>

            {/* Right zone — window controls */}
            <div className="flex items-center h-full" onMouseDown={(e) => e.stopPropagation()}>
                {/* Minimize */}
                <button
                    onClick={() => send("minimize")}
                    className={`w-10 h-full flex items-center justify-center transition-colors ${theme.text} ${theme.hoverBtn}`}
                >
                    <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M20 12H4" />
                    </svg>
                </button>

                {/* Maximize / Restore */}
                <button
                    onClick={() => { send("maximize"); setIsMax(!isMax); }}
                    className={`w-10 h-full flex items-center justify-center transition-colors ${theme.text} ${theme.hoverBtn}`}
                >
                    {isMax ? (
                        <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z" />
                        </svg>
                    ) : (
                        <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 8V4m0 0h4M4 4l5 5m11-1V4m0 0h-4m4 0l-5 5M4 16v4m0 0h4m-4 0l5-5m11 5l-5-5m5 5v-4m0 4h-4" />
                        </svg>
                    )}
                </button>

                {/* Close */}
                <button
                    onClick={() => send("close")}
                    className={`w-10 h-full flex items-center justify-center transition-colors ${theme.text} ${theme.hoverClose}`}
                >
                    <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                    </svg>
                </button>
            </div>
        </div>
    );
}