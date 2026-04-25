// ---------------------------------------------------------------
// Sidebar — Search input, recent stored entries, and MCP status
// indicator. Communicates with the WPF host via webview postMessage.
// ---------------------------------------------------------------

import { useState, useEffect, useCallback } from "react";

interface Props {
    results: any[];
    isLoading?: boolean;
    isDark?: boolean;
    onSearch?: (query: string) => void;
    onClose?: () => void;
}

// ---------- Icon components ----------

function IconSearch({ className = "w-4 h-4" }: { className?: string }) {
    return (
        <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <circle cx="11" cy="11" r="8" />
            <path d="m21 21-4.3-4.3" />
        </svg>
    );
}

function IconArrowRight({ className = "w-4 h-4" }: { className?: string }) {
    return (
        <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M5 12h14" /><path d="m12 5 7 7-7 7" />
        </svg>
    );
}

function IconClose({ className = "w-4 h-4" }: { className?: string }) {
    return (
        <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M18 6 6 18" /><path d="m6 6 12 12" />
        </svg>
    );
}

// ---------- Main component ----------

/**
 * Sidebar panel with search input, recent stored conversations, search results,
 * and a live MCP connection indicator.
 */
export default function Sidebar({ results, isLoading, isDark = false, onSearch, onClose }: Props) {
    const [query, setQuery] = useState("");
    const [recentStored, setRecentStored] = useState<any[]>([]);
    const [isFocused, setIsFocused] = useState(false);
    const [mcpConnected, setMcpConnected] = useState(false);

    // Subscribe to backend messages and poll for updates every 5 seconds.
    useEffect(() => {
        const webview = (window as any).chrome?.webview;
        if (!webview) return;

        const handler = (e: MessageEvent) => {
            if (typeof e.data !== "string") return;
            if (e.data.startsWith("recent_stored|"))
                setRecentStored(JSON.parse(e.data.substring(14)));
            if (e.data.startsWith("mcp_status|"))
                setMcpConnected(e.data.substring(11) === "connected");
        };

        webview.addEventListener("message", handler);
        webview.postMessage("get_recent_stored");
        webview.postMessage("get_mcp_status");

        const interval = setInterval(() => {
            webview.postMessage("get_recent_stored");
            webview.postMessage("get_mcp_status");
        }, 5000);

        return () => {
            webview.removeEventListener("message", handler);
            clearInterval(interval);
        };
    }, []);

    const handleSearch = useCallback((e?: React.FormEvent) => {
        if (e) e.preventDefault();
        if (!query.trim()) return;
        onSearch?.(query);
    }, [query, onSearch]);

    const clearQuery = useCallback(() => {
        setQuery("");
    }, []);

    const hasResults = results && results.length > 0;

    // Theme-dependent class sets
    const theme = {
        bg: isDark ? "bg-slate-900" : "bg-white",
        bgSecondary: isDark ? "bg-slate-950/50" : "bg-slate-50/50",
        border: isDark ? "border-slate-800" : "border-slate-200/80",
        textPrimary: isDark ? "text-slate-200" : "text-slate-800",
        textSecondary: isDark ? "text-slate-400" : "text-slate-500",
        textMuted: isDark ? "text-slate-500" : "text-slate-400",
        input: {
            base: isDark
                ? "bg-slate-950 border-slate-800 text-slate-200 placeholder-slate-600"
                : "bg-slate-50 border-slate-200 text-slate-700 placeholder-slate-400",
            focus: "focus:border-indigo-500/50 focus:ring-2 focus:ring-indigo-500/10",
            active: isFocused ? (isDark ? "border-indigo-500/50 ring-2 ring-indigo-500/10" : "border-indigo-400 ring-2 ring-indigo-100") : "",
        },
        card: isDark
            ? "bg-slate-900 border-slate-800 hover:border-indigo-500/30"
            : "bg-white border-slate-200/80 hover:border-indigo-300",
        badgeUser: isDark ? "bg-indigo-500/10 text-indigo-400" : "bg-indigo-50 text-indigo-600",
        badgeAssistant: isDark ? "bg-slate-800 text-slate-400" : "bg-slate-100 text-slate-600",
        skeleton: isDark ? "bg-slate-800" : "bg-slate-200",
        skeletonLight: isDark ? "bg-slate-800/50" : "bg-slate-100",
    };

    return (
        <aside className={`h-full flex flex-col w-full transition-colors duration-500 ${theme.bg} ${theme.border} border-r`}>
            {/* Header */}
            <div className={`p-4 border-b transition-colors duration-500 ${theme.border}`}>
                <div className="flex items-center justify-between mb-4">
                    <div className="flex items-center gap-3">
                        <div>
                            <h1 className={`text-base font-bold tracking-tight transition-colors duration-500 ${theme.textPrimary}`}>
                                Mnemos
                            </h1>
                            <p className={`text-[11px] font-medium transition-colors duration-500 ${theme.textMuted}`}>
                                Semantic memory
                            </p>
                        </div>
                    </div>
                    {onClose && (
                        <button
                            onClick={onClose}
                            className={`p-2 rounded-lg transition-all duration-300 ${
                                isDark
                                    ? "hover:bg-slate-800 text-slate-500 hover:text-slate-300"
                                    : "hover:bg-slate-100 text-slate-400 hover:text-slate-600"
                            }`}
                        >
                            <IconClose className="w-5 h-5" />
                        </button>
                    )}
                </div>

                {/* Search input */}
                <form onSubmit={handleSearch} className="relative">
                    <input
                        type="text"
                        value={query}
                        onChange={(e) => setQuery(e.target.value)}
                        onFocus={() => setIsFocused(true)}
                        onBlur={() => setIsFocused(false)}
                        placeholder="Search memory..."
                        className={`w-full rounded-xl py-2.5 pl-10 pr-10 text-sm outline-none transition-all duration-300 border ${theme.input.base} ${theme.input.focus} ${theme.input.active}`}
                    />
                    <span className={`absolute left-3.5 top-3 transition-colors duration-300 ${isFocused ? "text-indigo-500" : theme.textMuted}`}>
                        <IconSearch className="w-4 h-4" />
                    </span>

                    {query ? (
                        <div className="absolute right-2 top-1.5 flex items-center gap-1">
                            <button
                                type="button"
                                onClick={clearQuery}
                                className={`p-1.5 rounded-lg transition-all duration-200 ${
                                    isDark ? "hover:bg-slate-800 text-slate-500" : "hover:bg-slate-100 text-slate-400"
                                }`}
                            >
                                <IconClose className="w-3.5 h-3.5" />
                            </button>
                            <button
                                type="submit"
                                className={`p-1.5 rounded-lg transition-all duration-200 ${
                                    isDark
                                        ? "bg-indigo-500/10 text-indigo-400 hover:bg-indigo-500/20"
                                        : "bg-indigo-50 text-indigo-600 hover:bg-indigo-100"
                                }`}
                            >
                                <IconArrowRight className="w-4 h-4" />
                            </button>
                        </div>
                    ) : null}
                </form>
            </div>

            {/* Content area */}
            <div className={`flex-1 overflow-y-auto transition-colors duration-500 ${theme.bgSecondary}`}>
                <div className="p-4">
                    <div className="flex items-center justify-between mb-3">
                        <span className={`text-[11px] font-semibold uppercase tracking-wider transition-colors duration-500 ${
                            isDark ? "text-slate-500" : "text-slate-400"
                        }`}>
                            {hasResults ? "Results" : "Recent"}
                        </span>
                        {hasResults && (
                            <span className={`text-[11px] font-mono px-2 py-0.5 rounded-full border transition-colors duration-500 ${
                                isDark
                                    ? "bg-slate-800 border-slate-700 text-slate-400"
                                    : "bg-white border-slate-200 text-slate-500"
                            }`}>
                                {results.length}
                            </span>
                        )}
                    </div>

                    {isLoading ? (
                        <div className="space-y-2">
                            {[1, 2, 3].map(i => (
                                <div key={i} className={`p-4 rounded-xl border transition-colors duration-500 ${theme.card}`}>
                                    <div className="flex gap-2 mb-3">
                                        <div className={`w-10 h-4 rounded-md animate-pulse ${theme.skeleton}`} />
                                        <div className={`w-16 h-4 rounded-md animate-pulse ${theme.skeletonLight} ml-auto`} />
                                    </div>
                                    <div className={`h-3 rounded animate-pulse ${theme.skeletonLight} w-3/4 mb-2`} />
                                    <div className={`h-3 rounded animate-pulse ${theme.skeletonLight} w-1/2`} />
                                </div>
                            ))}
                        </div>
                    ) : hasResults ? (
                        <div className="space-y-2">
                            {results.map((res, i) => (
                                <div
                                    key={i}
                                    onClick={() => window.chrome?.webview?.postMessage(`load_conv:${res.conversation_id}|${res.content?.substring(0, 30) || ""}`)}
                                    className={`group p-3 rounded-xl border cursor-pointer transition-all duration-300 active:scale-[0.98] ${theme.card} ${
                                        isDark ? "hover:shadow-lg hover:shadow-indigo-500/5" : "hover:shadow-sm hover:shadow-slate-200/50"
                                    }`}
                                >
                                    <div className="flex items-center gap-2 mb-2">
                                        <span className={`text-[10px] font-bold px-2 py-0.5 rounded-md transition-colors duration-300 ${
                                            res.role === 'User' ? theme.badgeUser : theme.badgeAssistant
                                        }`}>
                                            {res.role === 'User' ? 'YOU' : 'CLAUDE'}
                                        </span>
                                        <span className={`text-[10px] ml-auto transition-colors duration-300 ${theme.textMuted}`}>
                                            {res.date?.substring(0, 10)}
                                        </span>
                                    </div>
                                    <p className={`text-[13px] leading-relaxed line-clamp-2 transition-colors duration-300 ${
                                        isDark ? "text-slate-400 group-hover:text-slate-300" : "text-slate-600 group-hover:text-slate-700"
                                    }`}>
                                        {res.content}
                                    </p>
                                </div>
                            ))}
                        </div>
                    ) : recentStored.length > 0 ? (
                        <div className="space-y-2">
                            {recentStored.map((m, i) => (
                                <div
                                    key={i}
                                    onClick={() => window.chrome?.webview?.postMessage(`load_conv:${m.ConvUuid}|`)}
                                    className={`group p-3 rounded-xl border cursor-pointer transition-all duration-300 active:scale-[0.98] ${theme.card} ${
                                        isDark ? "hover:shadow-lg hover:shadow-indigo-500/5" : "hover:shadow-sm hover:shadow-slate-200/50"
                                    }`}
                                >
                                    <div className="flex items-center gap-2 mb-1">
                                        <span className={`text-[10px] font-bold px-2 py-0.5 rounded-md transition-colors duration-300 ${
                                            m.Sender === 'human' ? theme.badgeUser : theme.badgeAssistant
                                        }`}>
                                            {m.Sender === 'human' ? 'YOU' : 'CLAUDE'}
                                        </span>
                                        <span className={`text-[10px] ml-auto truncate max-w-[120px] transition-colors duration-300 ${theme.textMuted}`}>
                                            {m.ConvName}
                                        </span>
                                    </div>
                                    <p className={`text-[12px] line-clamp-2 transition-colors duration-300 ${
                                        isDark ? "text-slate-400 group-hover:text-slate-300" : "text-slate-600 group-hover:text-slate-700"
                                    }`}>
                                        {m.Preview}
                                    </p>
                                </div>
                            ))}
                        </div>
                    ) : (
                        <div className="text-center py-12">
                            <div className={`inline-flex items-center justify-center w-12 h-12 rounded-2xl mb-4 transition-colors duration-500 ${
                                isDark ? "bg-slate-800 text-slate-600" : "bg-slate-100 text-slate-300"
                            }`}>
                                <IconSearch className="w-6 h-6" />
                            </div>
                            <p className={`text-sm font-medium transition-colors duration-500 ${theme.textSecondary}`}>
                                Type something to search
                            </p>
                            <p className={`text-[11px] mt-1 transition-colors duration-500 ${theme.textMuted}`}>
                                Your memories will appear here
                            </p>
                        </div>
                    )}
                </div>
            </div>

            {/* Footer — MCP status */}
            <div className={`p-3 border-t transition-colors duration-500 ${theme.border} ${theme.bg}`}>
                <div className="flex items-center justify-between">
                    <div className="flex items-center gap-2">
                        <div className="w-1.5 h-1.5 rounded-full animate-pulse"
                             style={{ background: mcpConnected ? '#10b981' : '#ef4444' }} />
                        <span className={`text-[10px] font-medium ${theme.textMuted}`}>
                            {mcpConnected ? "MCP active" : "MCP inactive"}
                        </span>
                    </div>
                    <span className={`text-[10px] font-mono transition-colors duration-500 ${theme.textMuted}`}>
                        v1.0
                    </span>
                </div>
            </div>
        </aside>
    );
}