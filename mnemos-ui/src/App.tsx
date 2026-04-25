// ---------------------------------------------------------------
// App — Root layout with sidebar, view switcher (list / constellation),
// theme toggle, and responsive mobile support.
// ---------------------------------------------------------------

import { useState, useEffect, useCallback } from "react";
import TitleBar from "./components/TitleBar";
import Sidebar from "./components/Sidebar";
import ConversationView from "./components/ConversationView";
import UniverseView from "./components/UniverseView";
import { useWebView } from "./hooks/useWebView";
import type { View } from "./types";

const MIN_SIDEBAR_WIDTH = 280;
const MAX_SIDEBAR_WIDTH = 520;
const MOBILE_BREAKPOINT = 768;

// ---------- Inline icons ----------

function IconSun({ className = "w-4 h-4" }: { className?: string }) {
    return (
        <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <circle cx="12" cy="12" r="4" />
            <path d="M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M6.34 17.66l-1.41 1.41M19.07 4.93l-1.41 1.41" />
        </svg>
    );
}

function IconMoon({ className = "w-4 h-4" }: { className?: string }) {
    return (
        <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9Z" />
        </svg>
    );
}

function IconList({ className = "w-4 h-4" }: { className?: string }) {
    return (
        <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <line x1="8" y1="6" x2="21" y2="6" /><line x1="8" y1="12" x2="21" y2="12" /><line x1="8" y1="18" x2="21" y2="18" />
            <line x1="3" y1="6" x2="3.01" y2="6" /><line x1="3" y1="12" x2="3.01" y2="12" /><line x1="3" y1="18" x2="3.01" y2="18" />
        </svg>
    );
}

function IconConstellation({ className = "w-4 h-4" }: { className?: string }) {
    return (
        <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <circle cx="12" cy="12" r="1" /><circle cx="19" cy="5" r="1" /><circle cx="5" cy="19" r="1" /><circle cx="19" cy="19" r="1" /><circle cx="5" cy="5" r="1" />
            <path d="M12 12L5 5" opacity="0.4" /><path d="M12 12L19 5" opacity="0.4" /><path d="M12 12L5 19" opacity="0.4" /><path d="M12 12L19 19" opacity="0.4" />
        </svg>
    );
}

function IconMemory({ className = "w-5 h-5" }: { className?: string }) {
    return (
        <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
            <path d="M12 2L2 7l10 5 10-5-10-5z" /><path d="M2 17l10 5 10-5" /><path d="M2 12l10 5 10-5" />
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

// ---------- Views ----------

const VIEWS: { key: View; label: string; icon: React.ReactNode }[] = [
    { key: "list", label: "List", icon: <IconList /> },
    { key: "constellation", label: "Constellation", icon: <IconConstellation /> },
];

// ---------- App component ----------

/**
 * Root application component managing layout, theme, and view state.
 */
function App() {
    const {
        results,
        activeConv,
        isSearching,
        search,
        loadConversation,
        clearConversation,
    } = useWebView();

    const [currentView, setCurrentView] = useState<View>("list");
    const [isMobile, setIsMobile] = useState(false);
    const [isSidebarOpen, setIsSidebarOpen] = useState(true);
    const [sidebarWidth, setSidebarWidth] = useState(320);
    const [isResizing, setIsResizing] = useState(false);
    const [isDark, setIsDark] = useState(false);

    // Apply theme class and notify backend
    useEffect(() => {
        document.documentElement.classList.toggle("dark", isDark);
        (window as any).chrome?.webview?.postMessage(`theme:${isDark ? "dark" : "light"}`);
    }, [isDark]);

    // When a conversation is opened, switch back to list view
    useEffect(() => {
        if (activeConv) setCurrentView("list");
    }, [activeConv]);

    // Responsive detection
    useEffect(() => {
        const check = () => {
            const mobile = window.innerWidth < MOBILE_BREAKPOINT;
            setIsMobile(mobile);
            setIsSidebarOpen(!mobile);
        };
        check();
        window.addEventListener("resize", check);
        return () => window.removeEventListener("resize", check);
    }, []);

    // Sidebar resize logic (desktop only)
    useEffect(() => {
        if (!isResizing) return;
        const onMove = (e: MouseEvent) => {
            setSidebarWidth(Math.max(MIN_SIDEBAR_WIDTH, Math.min(MAX_SIDEBAR_WIDTH, e.clientX)));
        };
        const onUp = () => setIsResizing(false);
        document.addEventListener("mousemove", onMove);
        document.addEventListener("mouseup", onUp);
        document.body.style.cssText = "cursor:col-resize;user-select:none;pointer-events:none";
        return () => {
            document.removeEventListener("mousemove", onMove);
            document.removeEventListener("mouseup", onUp);
            document.body.style.cssText = "";
        };
    }, [isResizing]);

    const handleNodeClick = useCallback((id: string) => {
        loadConversation(id);
    }, [loadConversation]);

    const toggleSidebar = useCallback(() => setIsSidebarOpen((p) => !p), []);
    const closeSidebar = useCallback(() => setIsSidebarOpen(false), []);

    return (
        <div className={`h-screen w-screen flex flex-col overflow-hidden font-sans antialiased transition-colors duration-500 ${isDark ? "bg-slate-950 text-slate-200" : "bg-white text-slate-800"} selection:bg-indigo-500/20`}>
            <TitleBar onMenuClick={toggleSidebar} showMenu={isMobile} isDark={isDark} />

            <div className="flex-1 flex overflow-hidden relative">
                {/* Mobile overlay */}
                {isMobile && isSidebarOpen && (
                    <div className="fixed inset-0 bg-slate-950/40 backdrop-blur-[2px] z-40 transition-all duration-500" onClick={closeSidebar} />
                )}

                {/* Sidebar */}
                <aside
                    className={[
                        isMobile ? "fixed inset-y-0 left-0 z-50" : "relative flex shrink-0",
                        isMobile && !isSidebarOpen ? "-translate-x-full opacity-0" : "translate-x-0 opacity-100",
                        "transition-all duration-500 ease-[cubic-bezier(0.32,0.72,0,1)]",
                    ].join(" ")}
                    style={!isMobile ? { width: sidebarWidth } : { width: "85%", maxWidth: 380 }}
                >
                    <div className={`w-full h-full border-r shadow-[4px_0_24px_rgba(0,0,0,0.02)] transition-colors duration-500 ${isDark ? "bg-slate-900 border-slate-800" : "bg-white border-slate-200/80"}`}>
                        <Sidebar
                            results={results}
                            isLoading={isSearching}
                            isDark={isDark}
                            onSearch={(query) => search(query, "all")}
                            onClose={isMobile ? closeSidebar : undefined}
                        />
                    </div>

                    {/* Resize handle (desktop) */}
                    {!isMobile && (
                        <div onMouseDown={() => setIsResizing(true)} className="absolute right-0 top-0 bottom-0 w-3 cursor-col-resize z-10 group flex items-center justify-center">
                            <div className="w-[2px] h-8 rounded-full bg-slate-300/0 group-hover:bg-indigo-500/50 transition-all duration-300 group-hover:h-12" />
                        </div>
                    )}
                </aside>

                {/* Main content area */}
                <main className={`flex-1 flex flex-col min-w-0 transition-colors duration-500 ${isDark ? "bg-slate-950" : "bg-slate-50/50"}`}>
                    {/* View switcher + theme toggle */}
                    <header className={`h-14 border-b flex items-center px-5 justify-between shrink-0 backdrop-blur-xl transition-colors duration-500 ${isDark ? "bg-slate-900/60 border-slate-800" : "bg-white/60 border-slate-200/60"}`}>
                        <nav className={`flex p-1 rounded-xl gap-0.5 ${isDark ? "bg-slate-800/60" : "bg-slate-100/60"}`}>
                            {VIEWS.map((v) => {
                                const isActive = currentView === v.key;
                                return (
                                    <button
                                        key={v.key}
                                        onClick={() => setCurrentView(v.key)}
                                        className={`relative flex items-center gap-2 px-3.5 py-1.5 rounded-lg text-[13px] font-medium transition-all duration-300 ${
                                            isActive
                                                ? isDark ? "text-white" : "text-slate-900"
                                                : isDark ? "text-slate-400 hover:text-slate-200" : "text-slate-500 hover:text-slate-700 hover:bg-slate-200/40"
                                        }`}
                                    >
                                        {isActive && (
                                            <span className={`absolute inset-0 rounded-lg shadow-sm border transition-all duration-300 ${isDark ? "bg-slate-700 border-slate-600 shadow-slate-950" : "bg-white border-slate-200/80 shadow-slate-200/50"}`} />
                                        )}
                                        <span className="relative z-10">{v.icon}</span>
                                        <span className="relative z-10 hidden sm:inline">{v.label}</span>
                                    </button>
                                );
                            })}
                        </nav>

                        {/* Dark / Light toggle */}
                        <div className="flex items-center gap-2">
                            <button
                                onClick={() => setIsDark(!isDark)}
                                className={`flex items-center gap-2 px-3 py-1.5 rounded-xl border text-[12px] font-bold uppercase tracking-wider transition-all duration-500 shadow-sm ${
                                    isDark
                                        ? "bg-slate-800 border-slate-700 text-indigo-400 hover:bg-slate-700 hover:border-slate-600"
                                        : "bg-white border-slate-200 text-slate-500 hover:bg-slate-50 hover:border-slate-300"
                                }`}
                            >
                                {isDark ? (
                                    <>
                                        <IconSun className="w-3.5 h-3.5" />
                                        <span className="hidden xs:inline">Light</span>
                                    </>
                                ) : (
                                    <>
                                        <IconMoon className="w-3.5 h-3.5" />
                                        <span className="hidden xs:inline">Dark</span>
                                    </>
                                )}
                            </button>
                        </div>
                    </header>

                    {/* View container: constellation / conversation list */}
                    <div className="flex-1 overflow-hidden relative">
                        {/* Constellation view */}
                        <div className={`absolute inset-0 transition-all duration-700 ease-[cubic-bezier(0.32,0.72,0,1)] ${currentView === "constellation" ? "opacity-100 scale-100" : "opacity-0 scale-[1.02] pointer-events-none"}`}>
                            <UniverseView onConvClick={handleNodeClick} isDark={isDark} />
                        </div>

                        {/* List view */}
                        <div className={`absolute inset-0 transition-all duration-500 ease-[cubic-bezier(0.32,0.72,0,1)] ${currentView === "list" ? "opacity-100 translate-y-0" : "opacity-0 translate-y-4 pointer-events-none"}`}>
                            <div className="h-full overflow-y-auto">
                                {activeConv ? (
                                    <ConversationView conversation={activeConv} onBack={clearConversation} isDark={isDark} />
                                ) : isSearching ? (
                                    <LoadingState isDark={isDark} />
                                ) : (
                                    <EmptyState
                                        isDark={isDark}
                                        hasResults={results.length > 0}
                                        onOpenConstellation={() => setCurrentView("constellation")}
                                    />
                                )}
                            </div>
                        </div>
                    </div>
                </main>
            </div>
        </div>
    );
}

// ---------- Sub-components ----------

/** Spinning loader shown during search. */
function LoadingState({ isDark }: { isDark: boolean }) {
    return (
        <div className="h-full flex items-center justify-center p-8">
            <div className="text-center space-y-6 max-w-xs">
                <div className="relative w-12 h-12 mx-auto">
                    <div className={`absolute inset-0 rounded-full border-[3px] ${isDark ? "border-slate-800" : "border-slate-200"}`} />
                    <div className="absolute inset-0 rounded-full border-[3px] border-indigo-500 border-t-transparent animate-spin" />
                </div>
                <div className="space-y-1.5">
                    <p className={`text-sm font-semibold tracking-tight ${isDark ? "text-slate-300" : "text-slate-700"}`}>
                        Searching...
                    </p>
                    <p className="text-xs text-slate-500 font-medium">
                        Exploring your memories...
                    </p>
                </div>
            </div>
        </div>
    );
}

/** Empty state with prompt to search or open the constellation. */
function EmptyState({
                        isDark,
                        hasResults,
                        onOpenConstellation,
                    }: {
    isDark: boolean;
    hasResults: boolean;
    onOpenConstellation: () => void;
}) {
    return (
        <div className="h-full flex items-center justify-center p-8">
            <div className="text-center space-y-7 max-w-sm">
                <div className={`w-16 h-16 mx-auto rounded-2xl border flex items-center justify-center shadow-sm transition-colors duration-500 ${isDark ? "bg-slate-900 border-slate-800 text-slate-500" : "bg-gradient-to-br from-slate-100 to-slate-50 border-slate-200/80 text-slate-400"}`}>
                    <IconMemory className="w-7 h-7" />
                </div>

                <div className="space-y-2">
                    <h3 className={`text-lg font-semibold tracking-tight transition-colors duration-500 ${isDark ? "text-slate-200" : "text-slate-800"}`}>
                        {hasResults ? "Select a memory" : "Your memory is ready"}
                    </h3>
                    <p className={`text-sm leading-relaxed max-w-[260px] mx-auto transition-colors duration-500 ${isDark ? "text-slate-500" : "text-slate-500"}`}>
                        {hasResults
                            ? "Click a result to relive the conversation."
                            : "Search or explore the constellation."}
                    </p>
                </div>

                {!hasResults && (
                    <button
                        onClick={onOpenConstellation}
                        className={`group inline-flex items-center gap-2 px-4 py-2 border rounded-xl text-sm font-medium transition-all duration-300 ${
                            isDark
                                ? "bg-slate-900 border-slate-800 text-slate-300 hover:border-indigo-500/50 hover:text-indigo-400 shadow-slate-950"
                                : "bg-white border-slate-200/80 text-slate-700 hover:border-indigo-300 hover:text-indigo-600 shadow-slate-100"
                        } shadow-sm`}
                    >
                        <IconConstellation className={`w-4 h-4 transition-colors ${isDark ? "text-slate-600 group-hover:text-indigo-400" : "text-slate-400 group-hover:text-indigo-500"}`} />
                        <span>View constellation</span>
                        <IconArrowRight className={`w-3.5 h-3.5 transition-all group-hover:translate-x-0.5 ${isDark ? "text-slate-700 group-hover:text-indigo-400" : "text-slate-300 group-hover:text-indigo-400"}`} />
                    </button>
                )}
            </div>
        </div>
    );
}

export default App;