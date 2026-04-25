// ---------------------------------------------------------------
// ConversationView — Displays a full conversation thread with
// user/assistant bubbles, thinking blocks, and scroll-to-snippet.
// ---------------------------------------------------------------

import { useEffect, useRef } from "react";
import type { Conversation } from "../types";

interface Props {
    conversation: Conversation;
    onBack: () => void;
    isDark?: boolean;
}

/**
 * Renders a list of chat messages with avatars, timestamps, and
 * optional thinking blocks in a dark/light themed container.
 */
export default function ConversationView({ conversation, onBack, isDark = false }: Props) {
    const endRef = useRef<HTMLDivElement>(null);

    // Scroll to a target snippet if provided, otherwise to the bottom.
    useEffect(() => {
        if (!conversation.target_snippet) {
            endRef.current?.scrollIntoView({ behavior: "smooth" });
            return;
        }

        const timer = setTimeout(() => {
            const el = Array.from(document.querySelectorAll(".msg-bubble")).find(
                (b) => b.textContent?.includes(conversation.target_snippet!)
            );
            if (el) {
                el.scrollIntoView({ behavior: "smooth", block: "center" });
                el.classList.add("ring-2", "ring-indigo-500/50");
                if (!isDark) el.classList.add("bg-indigo-50/50");
                else el.classList.add("bg-indigo-500/5");
                // Remove highlight after 3 seconds
                setTimeout(() => {
                    el.classList.remove("ring-2", "ring-indigo-500/50", "bg-indigo-50/50", "bg-indigo-500/5");
                }, 3000);
            } else {
                endRef.current?.scrollIntoView({ behavior: "smooth" });
            }
        }, 100);
        return () => clearTimeout(timer);
    }, [conversation, isDark]);

    // Theme-dependent class sets
    const theme = {
        headerBorder: isDark ? "border-slate-800" : "border-slate-100",
        backBtn: isDark
            ? "hover:bg-slate-800 text-slate-500 hover:text-slate-300"
            : "hover:bg-slate-100 text-slate-400 hover:text-slate-600",
        metaBadge: isDark
            ? "bg-slate-800 text-slate-400 border-slate-700"
            : "bg-slate-100 text-slate-400 border-slate-200",
        userAvatar: isDark
            ? "bg-indigo-500/10 text-indigo-400"
            : "bg-indigo-50 text-indigo-600",
        assistantAvatar: isDark
            ? "bg-slate-800 text-indigo-300"
            : "bg-slate-100 text-indigo-500",
        userBubble: isDark
            ? "bg-indigo-500/5 border-indigo-500/20 rounded-tr-sm"
            : "bg-indigo-50/60 border-indigo-100 rounded-tr-sm",
        assistantBubble: isDark
            ? "bg-slate-900 border-slate-800 rounded-tl-sm shadow-none"
            : "bg-white border-slate-200/80 rounded-tl-sm shadow-sm",
        userLabel: isDark ? "text-indigo-400" : "text-indigo-600",
        assistantLabel: isDark ? "text-indigo-300" : "text-indigo-500",
        thinking: isDark
            ? "bg-slate-800/50 border-l-2 border-slate-700"
            : "bg-slate-50 border-l-2 border-slate-300",
        thinkingText: isDark ? "text-slate-500" : "text-slate-500",
        content: isDark ? "text-slate-300" : "text-slate-700",
        date: isDark ? "text-slate-500" : "text-slate-400",
    };

    return (
        <div className="h-full flex flex-col">
            {/* Header */}
            <div className={`px-4 py-3 border-b flex items-center gap-3 shrink-0 transition-colors duration-500 ${theme.headerBorder}`}>
                <button
                    onClick={onBack}
                    className={`p-1.5 rounded-lg transition-colors ${theme.backBtn}`}
                    aria-label="Back"
                >
                    <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 19l-7-7m0 0l7-7m-7 7h18" />
                    </svg>
                </button>
                <span className={`text-xs font-mono px-2 py-1 rounded-full border transition-colors duration-500 ${theme.metaBadge}`}>
                    {conversation.messages.length} messages
                </span>
            </div>

            {/* Messages */}
            <div className="flex-1 overflow-y-auto">
                <div className="max-w-3xl mx-auto p-4 md:p-8 pb-20 space-y-6">
                    {conversation.messages.map((msg) => {
                        const isUser = msg.role === "User";
                        return (
                            <div key={msg.id} className={`flex gap-3 md:gap-4 ${isUser ? "flex-row-reverse" : "flex-row"}`}>
                                {/* Avatar */}
                                <div className={`w-8 h-8 rounded-full flex items-center justify-center text-xs font-bold shrink-0 transition-colors duration-500 ${
                                    isUser ? theme.userAvatar : theme.assistantAvatar
                                }`}>
                                    {isUser ? "U" : "A"}
                                </div>

                                {/* Bubble */}
                                <div className="msg-bubble flex-1 min-w-0">
                                    <div className={`p-4 rounded-2xl inline-block max-w-full border transition-all duration-500 ${
                                        isUser ? theme.userBubble : theme.assistantBubble
                                    }`}>
                                        {/* Meta line */}
                                        <div className="flex items-center gap-2 mb-2 flex-wrap">
                                            <span className={`text-[10px] font-semibold uppercase tracking-wider transition-colors duration-500 ${
                                                isUser ? theme.userLabel : theme.assistantLabel
                                            }`}>
                                                {msg.role}
                                            </span>
                                            {msg.date && (
                                                <span className={`text-[10px] transition-colors duration-500 ${theme.date}`}>
                                                    {msg.date.substring(0, 16).replace("T", " ")}
                                                </span>
                                            )}
                                        </div>

                                        {/* Thinking block (Claude only) */}
                                        {msg.thinking && (
                                            <div className={`mb-3 p-3 rounded-lg transition-colors duration-500 ${theme.thinking}`}>
                                                <p className={`text-[11px] italic leading-relaxed transition-colors duration-500 ${theme.thinkingText}`}>
                                                    {msg.thinking}
                                                </p>
                                            </div>
                                        )}

                                        {/* Message content */}
                                        <div className={`text-[13px] leading-7 whitespace-pre-wrap break-words transition-colors duration-500 ${theme.content}`}>
                                            {msg.content}
                                        </div>
                                    </div>
                                </div>
                            </div>
                        );
                    })}
                    {/* Invisible anchor for auto-scroll */}
                    <div ref={endRef} />
                </div>
            </div>
        </div>
    );
}