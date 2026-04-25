// ---------------------------------------------------------------
// UniverseView — 3D semantic constellation rendered with Three.js.
// UMAP-projected points with cluster colors, time scrubber,
// bloom effect, search, and detail panel.
// ---------------------------------------------------------------

import { useEffect, useRef, useState } from "react";
import * as THREE from "three";
// @ts-ignore
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls";
// @ts-ignore
import { EffectComposer } from "three/examples/jsm/postprocessing/EffectComposer";
// @ts-ignore
import { RenderPass } from "three/examples/jsm/postprocessing/RenderPass";
// @ts-ignore
import { UnrealBloomPass } from "three/examples/jsm/postprocessing/UnrealBloomPass";

// ---------- Inline icons ----------

function IconSearch({ className = "w-4 h-4" }: { className?: string }) {
    return (
        <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <circle cx="11" cy="11" r="8" /><path d="m21 21-4.3-4.3" />
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

function IconSparkles({ className = "w-4 h-4" }: { className?: string }) {
    return (
        <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="m12 3-1.912 5.813a2 2 0 0 1-1.275 1.275L3 12l5.813 1.912a2 2 0 0 1 1.275 1.275L12 21l1.912-5.813a2 2 0 0 1 1.275-1.275L21 12l-5.813-1.912a2 2 0 0 1-1.275-1.275L12 3Z" />
            <path d="M5 3v4" /><path d="M9 5H5" /><path d="M19 18v4" /><path d="M15 20h4" />
        </svg>
    );
}

function IconPlay({ className = "w-4 h-4" }: { className?: string }) {
    return (
        <svg className={className} viewBox="0 0 24 24" fill="currentColor">
            <path d="M8 5v14l11-7z" />
        </svg>
    );
}

function IconPause({ className = "w-4 h-4" }: { className?: string }) {
    return (
        <svg className={className} viewBox="0 0 24 24" fill="currentColor">
            <path d="M6 19h4V5H6v14zm8-14v14h4V5h-4z" />
        </svg>
    );
}

function IconMessage({ className = "w-4 h-4" }: { className?: string }) {
    return (
        <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z" />
        </svg>
    );
}

function IconMenu({ className = "w-4 h-4" }: { className?: string }) {
    return (
        <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M4 6h16M4 12h16M4 18h16" />
        </svg>
    );
}

// ---------- Types ----------

interface UniversePoint {
    Uuid: string;
    ConversationUuid: string;
    Sender: string;
    Preview: string;
    X: number;
    Y: number;
    Z: number;
    Date: string;
    ClusterId: number;
    ClusterName: string;
    FullText?: string;
}

interface Props {
    /** Callback when the user clicks "Open conversation". */
    onConvClick: (id: string) => void;
    /** Whether dark mode is active. */
    isDark?: boolean;
}

// ---------- Constants ----------

const BG_LIGHT = "#f8fafc";
const BG_DARK = "#0f172a";
const MUTED_COLOR = new THREE.Color("#475569");

const CLUSTER_COLORS = [
    new THREE.Color("#3b82f6"), new THREE.Color("#10b981"), new THREE.Color("#f59e0b"), new THREE.Color("#ef4444"),
    new THREE.Color("#8b5cf6"), new THREE.Color("#06b6d4"), new THREE.Color("#f97316"), new THREE.Color("#84cc16"),
    new THREE.Color("#ec4899"), new THREE.Color("#6366f1"), new THREE.Color("#14b8a6"), new THREE.Color("#f43f5e"),
    new THREE.Color("#d946ef"), new THREE.Color("#0ea5e9"), new THREE.Color("#22c55e"), new THREE.Color("#eab308"),
    new THREE.Color("#a855f7"), new THREE.Color("#38bdf8"), new THREE.Color("#ea580c"), new THREE.Color("#059669"),
    new THREE.Color("#4f46e5"), new THREE.Color("#be123c"), new THREE.Color("#c026d3"), new THREE.Color("#4338ca"),
];

const SCALE = 100;

// ---------- Component ----------

export default function UniverseView({ onConvClick, isDark = false }: Props) {
    const mountRef = useRef<HTMLDivElement>(null);
    const tooltipRef = useRef<HTMLDivElement>(null);
    const searchInputRef = useRef<HTMLInputElement>(null);
    const pointsRef = useRef<UniversePoint[]>([]);

    const sceneActions = useRef<{
        focusOn: (x: number, y: number, z: number, zoom: number) => void;
        updateVisuals: (themeId: number | null, maxTime: number, query: string, selectedId: string | null) => void;
        updateTheme: (dark: boolean, glow: boolean) => void;
    } | null>(null);

    const [points, setPoints] = useState<UniversePoint[]>([]);
    const [isLoading, setIsLoading] = useState(true);
    const [stats, setStats] = useState({ total: 0, convs: 0, labeled: 0 });
    const [debugMsg, setDebugMsg] = useState("Initializing...");

    const [activeTheme, setActiveTheme] = useState<number | null>(null);
    const [timeRange, setTimeRange] = useState({ min: 0, max: 100 });
    const [timeCursor, setTimeCursor] = useState(100);
    const [isPlaying, setIsPlaying] = useState(false);
    const [glowEnabled, setGlowEnabled] = useState(false);
    const [searchQuery, setSearchQuery] = useState("");
    const [selectedPoint, setSelectedPoint] = useState<UniversePoint | null>(null);
    const [showLegend, setShowLegend] = useState(false);

    const filtersRef = useRef({ activeTheme, searchQuery, selectedPoint });
    useEffect(() => {
        filtersRef.current = { activeTheme, searchQuery, selectedPoint };
    }, [activeTheme, searchQuery, selectedPoint]);

    const t = {
        bg: isDark ? "bg-slate-950" : "bg-slate-50",
        panel: isDark ? "bg-slate-900/90 border-slate-800" : "bg-white/95 border-slate-200/80",
        panelSolid: isDark ? "bg-slate-900 border-slate-800" : "bg-white border-slate-200/80",
        textPrimary: isDark ? "text-slate-200" : "text-slate-800",
        textSecondary: isDark ? "text-slate-400" : "text-slate-500",
        textMuted: isDark ? "text-slate-500" : "text-slate-400",
        accent: isDark ? "text-indigo-400" : "text-indigo-600",
        badgeUser: isDark ? "bg-indigo-500/10 text-indigo-400" : "bg-indigo-50 text-indigo-600",
        badgeAssistant: isDark ? "bg-slate-800 text-slate-400" : "bg-slate-100 text-slate-600",
        progressTrack: isDark ? "bg-slate-800" : "bg-slate-100",
    };

    useEffect(() => {
        const handleKeyDown = (e: KeyboardEvent) => {
            if ((e.ctrlKey || e.metaKey) && e.key === "k") {
                e.preventDefault();
                searchInputRef.current?.focus();
            }
            if (e.key === "Escape") {
                if (selectedPoint) handleCloseDetail();
                if (showLegend) setShowLegend(false);
            }
        };
        window.addEventListener("keydown", handleKeyDown);
        return () => window.removeEventListener("keydown", handleKeyDown);
    }, [selectedPoint, showLegend]);

    useEffect(() => {
        const webview = (window as any).chrome?.webview;
        if (!webview) {
            setDebugMsg("WebView unavailable");
            setIsLoading(false);
            return;
        }

        let initialLoadDone = false;
        const handler = (e: MessageEvent) => {
            if (typeof e.data !== "string") return;
            const sep = e.data.indexOf("|");
            if (sep === -1) return;
            const type = e.data.substring(0, sep);
            const raw = e.data.substring(sep + 1);

            if (type === "universe_points") {
                try {
                    const payload = JSON.parse(raw);
                    if (!payload) return;
                    const pts: UniversePoint[] = payload.Points || [];
                    pointsRef.current = pts;

                    if (pts.length === 0 && !initialLoadDone) {
                        initialLoadDone = true;
                        webview.postMessage("compute_umap");
                        return;
                    }

                    if (pts.length > 0) {
                        const timestamps = pts.map((p) => {
                            const t = new Date(p.Date).getTime();
                            return isNaN(t) ? Date.now() : t;
                        });
                        const minT = Math.min(...timestamps);
                        const maxT = Math.max(...timestamps);
                        setTimeRange({ min: minT === maxT ? minT - 1000 : minT, max: maxT });
                        setTimeCursor(maxT);
                    }

                    setPoints(pts);
                    setIsLoading(false);
                    setStats({
                        total: payload.TotalMessages || pts.length,
                        convs: payload.TotalConvs || 0,
                        labeled: pts.length,
                    });
                } catch {
                    setIsLoading(false);
                }
            }
        };

        webview.addEventListener("message", handler);
        webview.postMessage("get_universe_points");
        const autoUpdater = setInterval(() => webview.postMessage("compute_umap"), 30000);
        return () => {
            webview.removeEventListener("message", handler);
            clearInterval(autoUpdater);
        };
    }, []);

    useEffect(() => {
        let req: number;
        let lastUpdate = performance.now();
        const durationMs = 12000;
        const step = () => {
            const now = performance.now();
            const dt = now - lastUpdate;
            lastUpdate = now;
            if (isPlaying) {
                setTimeCursor((prev) => {
                    const next = prev + (timeRange.max - timeRange.min) * (dt / durationMs);
                    if (next >= timeRange.max) {
                        setIsPlaying(false);
                        return timeRange.max;
                    }
                    return next;
                });
            }
            req = requestAnimationFrame(step);
        };
        if (isPlaying) req = requestAnimationFrame(step);
        return () => cancelAnimationFrame(req);
    }, [isPlaying, timeRange]);

    useEffect(() => {
        sceneActions.current?.updateVisuals(activeTheme, timeCursor, searchQuery, selectedPoint?.Uuid || null);
    }, [activeTheme, timeCursor, searchQuery, selectedPoint]);

    useEffect(() => {
        sceneActions.current?.updateTheme(isDark, glowEnabled);
    }, [isDark, glowEnabled]);

    const handleCloseDetail = () => {
        setSelectedPoint(null);
        sceneActions.current?.focusOn(0, 0, 0, 200);
    };

    useEffect(() => {
        if (!mountRef.current || points.length === 0) return;

        const width = mountRef.current.clientWidth;
        const height = mountRef.current.clientHeight;

        const scene = new THREE.Scene();
        scene.background = new THREE.Color(isDark ? BG_DARK : BG_LIGHT);
        scene.fog = new THREE.FogExp2(isDark ? BG_DARK : BG_LIGHT, 0.002);

        const camera = new THREE.PerspectiveCamera(60, width / height, 0.1, 1000);
        camera.position.z = 200;

        const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false });
        renderer.setSize(width, height);
        renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
        mountRef.current.appendChild(renderer.domElement);

        const controls = new OrbitControls(camera, renderer.domElement);
        controls.enableDamping = true;
        controls.dampingFactor = 0.05;
        controls.maxDistance = 400;
        controls.minDistance = 5;
        controls.autoRotate = true;
        // FIX: Increased rotation speed so it's not too slow
        controls.autoRotateSpeed = 2.5;

        const renderScene = new RenderPass(scene, camera);
        const bloomPass = new UnrealBloomPass(new THREE.Vector2(width, height), 1.5, 0.4, 0.85);
        bloomPass.threshold = 0.0;
        bloomPass.strength = isDark ? 1.8 : 0.5; // Initial bloom strength based on theme
        bloomPass.radius = 0.5;
        bloomPass.enabled = false;

        const composer = new EffectComposer(renderer);
        composer.addPass(renderScene);
        composer.addPass(bloomPass);

        let targetFocus = new THREE.Vector3(0, 0, 0);
        let targetCamPos = new THREE.Vector3(0, 0, 200);
        let isFlying = false;
        let idleTimer: ReturnType<typeof setTimeout> | null = null;

        const onInteractionStart = () => {
            isFlying = false;
            controls.autoRotate = false;
            if (idleTimer) clearTimeout(idleTimer);
        };
        const onInteractionEnd = () => {
            if (idleTimer) clearTimeout(idleTimer);
            if (!filtersRef.current.selectedPoint) {
                idleTimer = setTimeout(() => {
                    controls.autoRotate = true;
                }, 1000);
            }
        };
        controls.addEventListener("start", onInteractionStart);
        controls.addEventListener("end", onInteractionEnd);

        const positions = new Float32Array(points.length * 3);
        const colors = new Float32Array(points.length * 3);

        points.forEach((p, i) => {
            positions[i * 3] = p.X * SCALE;
            positions[i * 3 + 1] = p.Y * SCALE;
            positions[i * 3 + 2] = p.Z * SCALE;
            const clusterIdx = typeof p.ClusterId === "number" ? p.ClusterId : 0;
            const color = CLUSTER_COLORS[clusterIdx % CLUSTER_COLORS.length];
            colors[i * 3] = color.r;
            colors[i * 3 + 1] = color.g;
            colors[i * 3 + 2] = color.b;
        });

        const geometry = new THREE.BufferGeometry();
        geometry.setAttribute("position", new THREE.BufferAttribute(positions, 3));
        geometry.setAttribute("color", new THREE.BufferAttribute(colors, 3));

        // FIX: Create a circular texture so points are circles, not squares
        const createCircleTexture = () => {
            const canvas = document.createElement("canvas");
            canvas.width = 64;
            canvas.height = 64;
            const ctx = canvas.getContext("2d");
            if (ctx) {
                ctx.beginPath();
                ctx.arc(32, 32, 30, 0, Math.PI * 2);
                ctx.fillStyle = "#ffffff";
                ctx.fill();
            }
            return new THREE.CanvasTexture(canvas);
        };

        const material = new THREE.PointsMaterial({
            size: 4, // Slightly larger because we cropped the edges
            map: createCircleTexture(),
            alphaTest: 0.1, // This removes the black/transparent square edges
            vertexColors: true,
            sizeAttenuation: true,
            transparent: true,
            opacity: isDark ? 1.0 : 0.85,
        });

        const particles = new THREE.Points(geometry, material);
        scene.add(particles);

        sceneActions.current = {
            focusOn: (x, y, z, zoomDistance) => {
                isFlying = true;
                controls.autoRotate = false;
                if (idleTimer) clearTimeout(idleTimer);

                targetFocus.set(0, 0, 0);
                const p = new THREE.Vector3(x, y, z);
                const dir = p.clone().normalize();
                if (dir.lengthSq() === 0) dir.set(0, 0, 1);
                targetCamPos.copy(p).add(dir.multiplyScalar(zoomDistance));
                if (zoomDistance > 50) {
                    idleTimer = setTimeout(() => {
                        targetFocus.set(0, 0, 0);
                        targetCamPos.copy(camera.position);
                        isFlying = true;
                        controls.autoRotate = true;
                    }, 1000);
                }
            },
            updateVisuals: (themeId, maxTime, query, selectedId) => {
                const lowerQuery = query.toLowerCase();
                const colorAttr = geometry.attributes.color as THREE.BufferAttribute;
                const posAttr = geometry.attributes.position as THREE.BufferAttribute;

                points.forEach((p, i) => {
                    const pTime = new Date(p.Date || Date.now()).getTime();
                    if (pTime <= maxTime) {
                        posAttr.setXYZ(i, p.X * SCALE, p.Y * SCALE, p.Z * SCALE);
                        const isThemeMatch = themeId === null || p.ClusterId === themeId;
                        const isSearchMatch =
                            lowerQuery === "" ||
                            p.Preview.toLowerCase().includes(lowerQuery) ||
                            p.ClusterName.toLowerCase().includes(lowerQuery);
                        const isSelectedFocus = selectedId === null || p.Uuid === selectedId;

                        if (isThemeMatch && isSearchMatch && isSelectedFocus) {
                            const cIdx = typeof p.ClusterId === "number" ? p.ClusterId : 0;
                            const baseColor = CLUSTER_COLORS[cIdx % CLUSTER_COLORS.length];
                            colorAttr.setXYZ(i, baseColor.r, baseColor.g, baseColor.b);
                        } else {
                            colorAttr.setXYZ(i, MUTED_COLOR.r, MUTED_COLOR.g, MUTED_COLOR.b);
                        }
                    } else {
                        posAttr.setXYZ(i, 99999, 99999, 99999);
                    }
                });
                colorAttr.needsUpdate = true;
                posAttr.needsUpdate = true;
            },
            updateTheme: (dark: boolean, glow: boolean) => {
                const targetColor = dark ? BG_DARK : BG_LIGHT;
                scene.background = new THREE.Color(targetColor);
                scene.fog = new THREE.FogExp2(targetColor, 0.002);
                bloomPass.enabled = glow;
                // FIX: Lower bloom intensity in light mode so it's readable
                bloomPass.strength = dark ? 1.8 : 0.5;
                material.opacity = dark ? 1.0 : 0.85;
            },
        };

        const raycaster = new THREE.Raycaster();
        raycaster.params.Points!.threshold = 2;
        const mouse = new THREE.Vector2();
        let pointerDownPos = new THREE.Vector2();
        renderer.domElement.addEventListener("pointerdown", (e) => pointerDownPos.set(e.clientX, e.clientY));

        const isPointValid = (p: UniversePoint) => {
            const f = filtersRef.current;
            const isThemeMatch = f.activeTheme === null || p.ClusterId === f.activeTheme;
            const lowerQuery = f.searchQuery.toLowerCase();
            const isSearchMatch =
                lowerQuery === "" ||
                p.Preview.toLowerCase().includes(lowerQuery) ||
                p.ClusterName.toLowerCase().includes(lowerQuery);
            const isSelectedFocus = f.selectedPoint === null || p.Uuid === f.selectedPoint.Uuid;
            return isThemeMatch && isSearchMatch && isSelectedFocus;
        };

        const handlePointerMove = (e: PointerEvent) => {
            const tip = tooltipRef.current;
            if (document.body.getAttribute("data-has-selection") === "true") {
                if (tip) tip.style.display = "none";
                document.body.style.cursor = "default";
                return;
            }

            const rect = renderer.domElement.getBoundingClientRect();
            mouse.x = ((e.clientX - rect.left) / rect.width) * 2 - 1;
            mouse.y = -((e.clientY - rect.top) / rect.height) * 2 + 1;
            raycaster.setFromCamera(mouse, camera);
            const intersects = raycaster.intersectObject(particles);

            if (intersects.length > 0) {
                const index = intersects[0].index;
                if (index !== undefined) {
                    const p = points[index];
                    if (!isPointValid(p)) {
                        document.body.style.cursor = "default";
                        if (tip) tip.style.display = "none";
                        return;
                    }
                    document.body.style.cursor = "pointer";
                    if (tip) {
                        tip.style.display = "block";
                        tip.style.left = `${e.clientX - rect.left + 12}px`;
                        tip.style.top = `${e.clientY - rect.top + 12}px`;
                        tip.innerHTML = `
                            <div style="font-size:10px; opacity:0.7; margin-bottom:2px">
                                ${p.Sender === "human" ? "You" : "Claude"} · <span style="font-weight:700">${p.ClusterName}</span>
                            </div>
                            <div style="font-size:12px; line-height:1.4; font-weight:500">
                                ${p.Preview}
                            </div>`;
                    }
                }
            } else {
                document.body.style.cursor = "default";
                if (tip) tip.style.display = "none";
            }
        };

        const handleClick = (e: MouseEvent) => {
            if (pointerDownPos.distanceTo(new THREE.Vector2(e.clientX, e.clientY)) > 5) return;
            raycaster.setFromCamera(mouse, camera);
            const intersects = raycaster.intersectObject(particles);

            if (intersects.length > 0 && intersects[0].index !== undefined) {
                const index = intersects[0].index;
                const p = points[index];
                if (!isPointValid(p)) {
                    window.dispatchEvent(new CustomEvent("pointSelected", { detail: null }));
                    sceneActions.current?.focusOn(0, 0, 0, 200);
                    return;
                }
                window.dispatchEvent(new CustomEvent("pointSelected", { detail: p }));
                sceneActions.current?.focusOn(p.X * SCALE, p.Y * SCALE, p.Z * SCALE, 20);
            } else {
                window.dispatchEvent(new CustomEvent("pointSelected", { detail: null }));
                sceneActions.current?.focusOn(0, 0, 0, 200);
            }
        };

        renderer.domElement.addEventListener("pointermove", handlePointerMove);
        renderer.domElement.addEventListener("click", handleClick);
        renderer.domElement.addEventListener("pointerleave", () => {
            if (tooltipRef.current) tooltipRef.current.style.display = "none";
        });

        window.addEventListener("resize", () => {
            if (!mountRef.current) return;
            const w = mountRef.current.clientWidth;
            const h = mountRef.current.clientHeight;
            camera.aspect = w / h;
            camera.updateProjectionMatrix();
            renderer.setSize(w, h);
            composer.setSize(w, h);
        });

        let frameId: number;
        const animate = () => {
            frameId = requestAnimationFrame(animate);
            if (isFlying) {
                controls.target.lerp(targetFocus, 0.08);
                camera.position.lerp(targetCamPos, 0.06);
                if (
                    controls.target.distanceToSquared(targetFocus) < 0.01 &&
                    camera.position.distanceToSquared(targetCamPos) < 0.01
                ) {
                    isFlying = false;
                }
            }
            controls.update();
            composer.render();
        };
        animate();

        return () => {
            cancelAnimationFrame(frameId);
            if (idleTimer) clearTimeout(idleTimer);
            controls.removeEventListener("start", onInteractionStart);
            controls.removeEventListener("end", onInteractionEnd);
            geometry.dispose();
            material.dispose();
            renderer.dispose();
            composer.dispose();
            if (mountRef.current && renderer.domElement) mountRef.current.removeChild(renderer.domElement);
        };
    }, [points, isDark]);

    useEffect(() => {
        const handlePointSelect = (e: any) => {
            const p = e.detail;
            setSelectedPoint(p);
            document.body.setAttribute("data-has-selection", p ? "true" : "false");
            if (p) setActiveTheme(null);
        };
        window.addEventListener("pointSelected", handlePointSelect);
        return () => window.removeEventListener("pointSelected", handlePointSelect);
    }, []);

    const handleThemeClick = (clusterId: number) => {
        handleCloseDetail();
        if (activeTheme === clusterId) {
            setActiveTheme(null);
            return;
        }
        setActiveTheme(clusterId);
        // FIX: Removed the sceneActions.current?.focusOn() call here
        // The camera won't move anymore when clicking on a legend topic
    };

    const visiblePoints = points.filter((p) => {
        const pTime = new Date(p.Date || Date.now()).getTime();
        if (pTime > timeCursor) return false;
        if (searchQuery) {
            const q = searchQuery.toLowerCase();
            return p.Preview.toLowerCase().includes(q) || p.ClusterName.toLowerCase().includes(q);
        }
        return true;
    });
    const visibleClusterIds = Array.from(new Set(visiblePoints.map((p) => p.ClusterId))).sort((a, b) => a - b);

    return (
        <div className={`w-full h-full relative overflow-hidden transition-colors duration-1000 ${t.bg}`}>
            <div ref={mountRef} className="w-full h-full outline-none" />

            {/* Loading overlay */}
            {isLoading && (
                <div className={`absolute inset-0 z-[100] flex items-center justify-center backdrop-blur-sm transition-all duration-500 ${isDark ? "bg-slate-950/70" : "bg-slate-50/70"}`}>
                    <div className={`border rounded-2xl shadow-2xl p-6 max-w-sm w-[90%] flex flex-col transition-colors duration-500 ${t.panelSolid}`}>
                        <div className="flex items-center gap-4">
                            <div className="relative w-10 h-10 flex-shrink-0">
                                <div className={`absolute inset-0 rounded-full border-2 ${isDark ? "border-slate-800" : "border-slate-100"}`} />
                                <div className="absolute inset-0 rounded-full border-2 border-indigo-500 border-t-transparent animate-spin" />
                                <span className="absolute inset-0 flex items-center justify-center">
                                    <IconSparkles className={`w-4 h-4 ${t.accent}`} />
                                </span>
                            </div>
                            <div>
                                <h2 className={`text-sm font-semibold tracking-tight ${t.textPrimary}`}>Spatial generation</h2>
                                <p className={`text-[11px] font-mono ${t.textMuted}`}>{debugMsg}</p>
                            </div>
                        </div>
                    </div>
                </div>
            )}

            {!isLoading && (
                <>
                    {/* Search bar */}
                    <div className={`absolute top-3 left-1/2 -translate-x-1/2 z-[110] transition-all duration-500 w-[min(320px,90%)] ${selectedPoint ? "opacity-0 -translate-y-6 pointer-events-none" : "opacity-100"}`}>
                        <div className={`flex items-center gap-2.5 px-3 py-2 rounded-xl backdrop-blur-md border shadow-lg transition-all duration-500 ${isDark ? "bg-slate-900/90 border-slate-800 text-slate-200 focus-within:border-indigo-500/50" : "bg-white/95 border-slate-200/80 text-slate-700 focus-within:border-indigo-400"}`}>
                            <IconSearch className={`w-4 h-4 flex-shrink-0 ${isDark ? "text-slate-500" : "text-slate-400"}`} />
                            <input
                                ref={searchInputRef}
                                type="text"
                                placeholder="Search... (Ctrl+K)"
                                value={searchQuery}
                                onChange={(e) => setSearchQuery(e.target.value)}
                                className="w-full bg-transparent border-none outline-none text-[13px] placeholder-slate-400"
                            />
                            {searchQuery && (
                                <button onClick={() => setSearchQuery("")} className={`transition-colors ${isDark ? "text-slate-500 hover:text-slate-300" : "text-slate-400 hover:text-slate-600"}`}>
                                    <IconClose className="w-3.5 h-3.5" />
                                </button>
                            )}
                        </div>
                    </div>

                    {/* Glow toggle */}
                    <button
                        onClick={() => setGlowEnabled(!glowEnabled)}
                        className={`absolute top-3 right-3 z-[110] flex items-center gap-1.5 px-2.5 py-1.5 rounded-lg border text-[10px] font-bold uppercase tracking-wider transition-all duration-500 shadow-sm ${selectedPoint ? "opacity-0 pointer-events-none" : "opacity-100"} ${glowEnabled ? (isDark ? "bg-indigo-500/10 text-indigo-400 border-indigo-500/30" : "bg-indigo-50 text-indigo-600 border-indigo-200") : (isDark ? "bg-slate-800 border-slate-700 text-slate-400 hover:bg-slate-700" : "bg-white border-slate-200 text-slate-500 hover:bg-slate-50")}`}
                    >
                        <IconSparkles className={`w-3 h-3 transition-all ${glowEnabled ? "scale-110" : "scale-100"}`} />
                        <span className="hidden sm:inline">Glow</span>
                    </button>

                    {/* Legend toggle (mobile) */}
                    <button
                        onClick={() => setShowLegend(!showLegend)}
                        className={`absolute top-3 right-14 sm:hidden z-[110] p-1.5 rounded-lg border transition-all ${isDark ? "bg-slate-800 border-slate-700 text-slate-400" : "bg-white border-slate-200 text-slate-500"}`}
                    >
                        <IconMenu className="w-4 h-4" />
                    </button>

                    {/* Tooltip */}
                    <div
                        ref={tooltipRef}
                        className={`absolute pointer-events-none z-50 max-w-[220px] backdrop-blur border rounded-lg px-3 py-2 shadow-lg transition-colors duration-500 ${isDark ? "bg-slate-900/95 border-slate-800 text-slate-200" : "bg-white/95 border-slate-200/80 text-slate-800"}`}
                        style={{ display: "none" }}
                    />

                    {/* Progress card */}
                    <div className={`absolute top-14 left-3 z-[110] transition-all duration-500 ${selectedPoint ? "opacity-0 -translate-x-6 pointer-events-none" : "opacity-100"}`}>
                        <div className={`backdrop-blur border rounded-xl p-3 shadow-sm w-[200px] transition-colors duration-500 ${t.panel}`}>
                            <div className="flex justify-between items-center mb-1">
                                <p className={`text-xs font-semibold ${t.textPrimary}`}>{stats.labeled.toLocaleString()} projected</p>
                                <span className={`text-xs font-black ${t.accent}`}>{Math.round((stats.labeled / (stats.total || 1)) * 100)}%</span>
                            </div>
                            <div className={`h-1 w-full rounded-full overflow-hidden mb-2 ${t.progressTrack}`}>
                                <div className="h-full bg-indigo-500 rounded-full transition-all duration-700" style={{ width: `${stats.total > 0 ? (stats.labeled / stats.total) * 100 : 0}%` }} />
                            </div>
                            <div className={`flex justify-between items-center text-[10px] ${t.textMuted}`}>
                                <span className="uppercase font-bold tracking-tight">{visibleClusterIds.length} topics</span>
                                <span>{stats.total.toLocaleString()} msgs</span>
                            </div>
                        </div>
                    </div>

                    {/* Cluster legend - FIX: Width changed from w-[210px] to w-[260px] */}
                    <div className={`absolute top-14 right-3 bottom-16 z-[110] transition-all duration-500 ${selectedPoint ? "opacity-0 translate-x-6 pointer-events-none" : "opacity-100"} ${showLegend ? "pointer-events-auto" : "pointer-events-none sm:pointer-events-auto"}`}>
                        <div className={`backdrop-blur-md border rounded-xl p-3 shadow-xl w-[260px] h-fit max-h-[calc(100%-60px)] flex flex-col transition-all duration-500 ${t.panel} ${showLegend ? "opacity-100 translate-x-0" : "opacity-0 translate-x-4 sm:opacity-100 sm:translate-x-0"}`}>
                            <p className={`text-[10px] uppercase font-black tracking-wider border-b pb-2 mb-2 shrink-0 ${isDark ? "text-slate-500 border-slate-800" : "text-slate-400 border-slate-100"}`}>
                                Topics
                            </p>
                            <div className="grid grid-cols-1 gap-0.5 overflow-y-auto pr-1 custom-scrollbar">
                                {visibleClusterIds.map((cid) => {
                                    const pt = points.find((p) => p.ClusterId === cid);
                                    const isActive = activeTheme === cid;
                                    const isMuted = activeTheme !== null && !isActive;
                                    return (
                                        <div
                                            key={cid}
                                            onClick={() => handleThemeClick(cid)}
                                            className={`flex items-center gap-2 group cursor-pointer px-2 py-1.5 rounded-md transition-all ${isActive ? (isDark ? "bg-slate-800" : "bg-slate-100") : isDark ? "hover:bg-slate-800/50" : "hover:bg-slate-50"}`}
                                        >
                                            <div
                                                className={`w-2.5 h-2.5 rounded-full flex-shrink-0 transition-transform ${isActive ? "scale-125" : "group-hover:scale-110"}`}
                                                style={{
                                                    backgroundColor: isMuted ? MUTED_COLOR.getStyle() : CLUSTER_COLORS[cid % CLUSTER_COLORS.length].getStyle(),
                                                    boxShadow: isDark && !isMuted ? `0 0 6px ${CLUSTER_COLORS[cid % CLUSTER_COLORS.length].getStyle()}` : "none",
                                                }}
                                            />
                                            <span className={`text-[11px] font-medium truncate leading-tight ${isActive ? (isDark ? "text-white" : "text-slate-900") : isMuted ? "text-slate-500" : isDark ? "text-slate-300" : "text-slate-600"}`}>
                                                {pt?.ClusterName || `Cluster ${cid + 1}`}
                                            </span>
                                        </div>
                                    );
                                })}
                            </div>
                        </div>
                    </div>

                    {/* Time scrubber */}
                    <div className={`absolute bottom-3 left-1/2 -translate-x-1/2 z-[110] transition-all duration-500 ${selectedPoint ? "opacity-0 translate-y-6 pointer-events-none" : "opacity-100"}`}>
                        <div className={`backdrop-blur-md border rounded-xl px-3 py-2 shadow-lg flex items-center gap-3 transition-colors duration-500 ${t.panel}`}>
                            <span className={`text-[10px] font-bold ${t.textMuted} hidden sm:inline`}>
                                {new Date(timeRange.min).toLocaleDateString("en-US", { month: "short", year: "2-digit" })}
                            </span>

                            <button
                                onClick={() => {
                                    if (timeCursor >= timeRange.max) setTimeCursor(timeRange.min);
                                    setIsPlaying(!isPlaying);
                                }}
                                className={`w-8 h-8 rounded-full flex items-center justify-center transition-colors shadow-md flex-shrink-0 text-white ${isDark ? "bg-indigo-600 hover:bg-indigo-500" : "bg-slate-800 hover:bg-slate-700"}`}
                            >
                                {isPlaying ? <IconPause className="w-3.5 h-3.5" /> : <IconPlay className="w-3.5 h-3.5 translate-x-0.5" />}
                            </button>

                            <div className="flex flex-col w-[140px] sm:w-[200px]">
                                <span className={`text-[10px] font-bold text-center mb-1 ${isDark ? "text-indigo-400" : "text-indigo-600"}`}>
                                    {new Date(timeCursor).toLocaleDateString("en-US", { day: "numeric", month: "short" })}
                                </span>
                                <input
                                    type="range"
                                    min={timeRange.min}
                                    max={timeRange.max}
                                    value={timeCursor}
                                    onChange={(e) => {
                                        setIsPlaying(false);
                                        setTimeCursor(Number(e.target.value));
                                    }}
                                    className={`w-full h-1.5 rounded-lg appearance-none cursor-pointer ${isDark ? "bg-slate-800 accent-indigo-500" : "bg-slate-200 accent-indigo-600"}`}
                                />
                            </div>

                            <span className={`text-[10px] font-bold ${t.textMuted} hidden sm:inline`}>
                                {new Date(timeRange.max).toLocaleDateString("en-US", { month: "short", year: "2-digit" })}
                            </span>
                        </div>
                    </div>

                    {/* Detail panel (slides in from the right) */}
                    <div className={`absolute inset-0 sm:inset-auto sm:top-3 sm:right-3 sm:bottom-3 sm:w-[380px] z-[120] transition-all duration-500 ${selectedPoint ? "translate-x-0 opacity-100 pointer-events-auto" : "translate-x-full opacity-0 pointer-events-none"}`}>
                        {selectedPoint && (
                            <div className={`h-full sm:h-auto sm:max-h-full flex flex-col sm:rounded-2xl shadow-2xl border backdrop-blur-xl p-4 sm:p-5 transition-colors duration-500 ${t.panel}`}>
                                <div className="flex items-start justify-between mb-4">
                                    <div className={`inline-flex items-center gap-2 px-2.5 py-1 rounded-full border ${isDark ? "bg-slate-800 border-slate-700" : "bg-slate-50 border-slate-200"}`}>
                                        <div
                                            className="w-2 h-2 rounded-full"
                                            style={{
                                                backgroundColor: CLUSTER_COLORS[(selectedPoint.ClusterId || 0) % CLUSTER_COLORS.length].getStyle(),
                                                boxShadow: isDark ? `0 0 6px ${CLUSTER_COLORS[(selectedPoint.ClusterId || 0) % CLUSTER_COLORS.length].getStyle()}` : "none",
                                            }}
                                        />
                                        <span className={`text-[10px] font-bold uppercase tracking-wider ${isDark ? "text-slate-300" : "text-slate-600"}`}>
                                            {selectedPoint.ClusterName || `Cluster ${selectedPoint.ClusterId + 1}`}
                                        </span>
                                    </div>
                                    <button onClick={handleCloseDetail} className={`p-1.5 rounded-full transition-colors ${isDark ? "hover:bg-slate-800 text-slate-400" : "hover:bg-slate-100 text-slate-500"}`}>
                                        <IconClose className="w-4 h-4" />
                                    </button>
                                </div>

                                <div className="mb-4">
                                    <div className="flex items-center gap-2 text-xs">
                                        <span className={`text-[10px] font-bold px-1.5 py-0.5 rounded ${selectedPoint.Sender === "human" ? t.badgeUser : t.badgeAssistant}`}>
                                            {selectedPoint.Sender === "human" ? "You" : "Claude"}
                                        </span>
                                        <span className={t.textMuted}>•</span>
                                        <span className={`text-[11px] ${t.textSecondary}`}>
                                            {new Date(selectedPoint.Date).toLocaleDateString("en-US", { day: "numeric", month: "short", year: "numeric" })}
                                        </span>
                                    </div>
                                </div>

                                <div className={`flex-1 overflow-y-auto pr-2 custom-scrollbar text-sm leading-relaxed whitespace-pre-wrap ${t.textSecondary}`}>
                                    {selectedPoint.FullText || selectedPoint.Preview}
                                </div>

                                <div className={`pt-4 mt-3 border-t ${isDark ? "border-slate-800" : "border-slate-100"}`}>
                                    <button
                                        onClick={() => onConvClick(selectedPoint.ConversationUuid)}
                                        className="w-full py-3 px-4 bg-indigo-600 hover:bg-indigo-500 text-white text-sm font-semibold rounded-xl shadow-lg shadow-indigo-500/30 transition-all active:scale-[0.98] flex items-center justify-center gap-2"
                                    >
                                        <IconMessage className="w-4 h-4" />
                                        <span className="hidden sm:inline">Open conversation</span>
                                        <span className="sm:hidden">Open</span>
                                    </button>
                                </div>
                            </div>
                        )}
                    </div>
                </>
            )}
        </div>
    );
}