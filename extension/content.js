// 각 프레임에 한 번만 주입되어 window.__areaFullscreen 으로 기능을 제공한다. (worker.js 에서 호출)
//   scan   : 이 프레임에서 가장 큰 동영상의 점수(보이는 면적)
//   arm    : 하위 iframe 의 꽉 채우기 요청을 받을 준비
//   fill   : 가장 큰 동영상을 화면(프레임)에 꽉 채우고, 상위 프레임에도 이 iframe 을 꽉 채우도록 요청
//            (동영상의 가로/세로 비율을 돌려준다. 영상 창의 처음 크기를 정할 때 쓴다)
//   unfill : 원래대로
//   mark / unmark : 도우미 프로그램이 창을 찾을 수 있게 제목 앞에 표시를 붙였다 뗀다
(() => {
    if (window.__areaFullscreen) return;

    // 화면을 꽉 채울 요소에 줄 스타일
    const FILL = {
        "position": "fixed", "left": "0", "top": "0", "right": "auto", "bottom": "auto",
        "width": "100vw", "height": "100vh", "min-width": "0", "min-height": "0",
        "max-width": "none", "max-height": "none", "margin": "0", "padding": "0", "border": "0",
        "transform": "none", "translate": "none", "scale": "none", "rotate": "none",
        "z-index": "2147483647", "visibility": "visible", "opacity": "1", "display": "block",
        "object-fit": "contain", "background": "#000", "clip-path": "none",
    };
    // 조상 요소가 position:fixed 의 기준이 되거나 화면을 가리지 않도록 푼다.
    const ANCESTOR = {
        "transform": "none", "translate": "none", "scale": "none", "rotate": "none",
        "filter": "none", "backdrop-filter": "none", "perspective": "none", "contain": "none",
        "container-type": "normal", "will-change": "auto", "clip-path": "none", "mask": "none",
        "opacity": "1", "z-index": "2147483647", "content-visibility": "visible",
    };
    // 페이지의 나머지는 숨긴다. (visibility 는 자식이 visible 로 덮어쓸 수 있다)
    const ROOT = {"overflow": "hidden", "background": "#000"};
    const BODY = {...ROOT, "visibility": "hidden"};

    // 유튜브처럼 창 크기가 바뀔 때마다 동영상의 style 속성을 다시 쓰는 사이트가 있어서,
    // 인라인 스타일 대신 요소에 표시(속성)를 달고 스타일시트로 적용한다.
    // :not(#\#) 를 붙여 사이트의 ID 선택자 규칙보다 우선순위를 높인다.
    const ATTR = "data-area-fullscreen";
    const BOOST = ":not(#\\#):not(#\\#):not(#\\#)";
    const rule = (token, styles) => `${BOOST}[${ATTR}~="${token}"]{` +
        Object.entries(styles).map(([name, value]) => `${name}:${value} !important`).join(";") + "}";
    // 사이트가 ::-webkit-media-controls 를 display:none 으로 숨겨 둔 경우에도 기본 컨트롤이 보이게 한다.
    // (표시/숨김 애니메이션은 브라우저에 맡기도록 display 만 되살린다)
    // 이 의사 요소 규칙은 앞에 video 선택자가 없으면 Chrome 이 적용하지 않는다.
    // (-panel 까지 건드리면 컨트롤이 아예 그려지지 않으므로 바깥 두 단계만 되살린다)
    const CONTROLS = ["", "-enclosure"].map(part =>
        `video${BOOST}[${ATTR}~="fill"]::-webkit-media-controls${part}{display:flex !important}`).join("\n");
    const CSS = [rule("ancestor", ANCESTOR), rule("root", ROOT), rule("body", BODY), rule("fill", FILL), CONTROLS].join("\n");

    const marked = new Set();
    const styleSheets = new Map();  // document 또는 shadow root → 넣어 둔 <style>
    let target = null;
    let originalControls = false;
    let originalControlsList = null;
    let controlsObserver = null;
    let armed = false;
    let watchTimer = null;
    let marker = null;
    let titleObserver = null;

    // 열린 shadow root 안의 동영상까지 찾는다.
    function findAll(selector, root = document, out = []) {
        root.querySelectorAll(selector).forEach(el => out.push(el));
        root.querySelectorAll("*").forEach(el => { if (el.shadowRoot) findAll(selector, el.shadowRoot, out); });
        return out;
    }

    function bestVideo() {
        let best = null, bestScore = 0;
        for (const video of findAll("video")) {
            const r = video.getBoundingClientRect();
            // 아직 재생할 데이터가 없는 동영상은 우선순위를 크게 낮춘다.
            const score = r.width * r.height * (video.readyState > 0 ? 1 : 0.001);
            if (score > bestScore) { best = video; bestScore = score; }
        }
        return best ? {video: best, score: bestScore} : null;
    }

    function ancestors(el) {
        const out = [];
        for (let node = el.parentNode; node; node = node.parentNode) {
            if (node.nodeType === Node.DOCUMENT_FRAGMENT_NODE && node.host) node = node.host;
            if (node.nodeType !== Node.ELEMENT_NODE) break;
            out.push(node);
        }
        return out;
    }

    function mark(el, token) {
        const tokens = new Set((el.getAttribute(ATTR) || "").split(" ").filter(Boolean));
        tokens.add(token);
        el.setAttribute(ATTR, [...tokens].join(" "));
        marked.add(el);

        // 요소가 들어 있는 문서(또는 shadow root)마다 스타일시트를 한 번씩 넣는다.
        const root = el.getRootNode();
        if (styleSheets.has(root)) return;
        const style = document.createElement("style");
        style.textContent = CSS;
        (root === document ? (document.head || document.documentElement) : root).append(style);
        styleSheets.set(root, style);
    }

    function apply(el) {
        clear();
        target = el;
        for (const a of ancestors(el)) mark(a, "ancestor");
        mark(document.documentElement, "root");
        if (document.body) mark(document.body, "body");
        mark(el, "fill");
        if (el.tagName === "VIDEO") {
            originalControls = el.controls;
            originalControlsList = el.getAttribute("controlslist");
            showControls(el);
            // 사이트가 controls 를 다시 끄면 바로 되돌린다.
            controlsObserver = new MutationObserver(() => showControls(el));
            controlsObserver.observe(el, {attributes: true, attributeFilter: ["controls", "controlslist"]});
        }
        if (window !== window.top) window.parent.postMessage({areaFullscreen: "fill"}, "*");
        document.addEventListener("keydown", onKeyDown, true);
    }

    // 기본 컨트롤을 켜되, 전체화면 버튼/더블클릭 전체화면은 영상 창을 모니터 전체로 끌어내므로 막는다.
    function showControls(video) {
        if (!video.controls) video.controls = true;
        const list = video.getAttribute("controlslist") || "";
        if (!list.split(" ").includes("nofullscreen")) video.setAttribute("controlslist", (list + " nofullscreen").trim());
    }

    function clear() {
        if (controlsObserver) controlsObserver.disconnect();
        controlsObserver = null;
        if (target && target.tagName === "VIDEO") {
            target.controls = originalControls;
            if (originalControlsList === null) target.removeAttribute("controlslist");
            else target.setAttribute("controlslist", originalControlsList);
        }
        for (const el of marked) el.removeAttribute(ATTR);
        marked.clear();
        for (const style of styleSheets.values()) style.remove();
        styleSheets.clear();
        target = null;
        document.removeEventListener("keydown", onKeyDown, true);
    }

    function onKeyDown(e) {
        if (e.key !== "Escape") return;
        e.stopPropagation();
        chrome.runtime.sendMessage({cmd: "exit"});
    }

    // 하위 iframe 안의 동영상이 꽉 채워지면, 그 iframe 을 이 프레임에서 꽉 채운다.
    window.addEventListener("message", e => {
        if (!armed || !e.data || e.data.areaFullscreen !== "fill") return;
        const frame = findAll("iframe, frame").find(f => f.contentWindow === e.source);
        if (frame) apply(frame);
    });

    function applyMarker() {
        if (marker && !document.title.startsWith(marker)) document.title = marker + document.title;
    }

    window.__areaFullscreen = {
        scan() {
            const best = bestVideo();
            return best ? best.score : 0;
        },
        arm() {
            armed = true;
        },
        fill() {
            const best = bestVideo();
            if (!best) return false;
            const video = best.video, rect = video.getBoundingClientRect();
            const ratio = video.videoWidth && video.videoHeight ? video.videoWidth / video.videoHeight
                : rect.width && rect.height ? rect.width / rect.height : 16 / 9;
            apply(video);
            // 사이트가 동영상 요소를 갈아 끼우면(다음 영상 등) 새 동영상을 다시 채우고,
            // 표시 속성이나 스타일시트를 지워 버리면 다시 붙인다.
            clearInterval(watchTimer);
            watchTimer = setInterval(() => {
                if (!target || target.tagName !== "VIDEO") return;
                if (!target.isConnected) {
                    const next = bestVideo();
                    if (next) apply(next.video);
                } else if (!target.hasAttribute(ATTR) || [...styleSheets.values()].some(s => !s.isConnected)) {
                    apply(target);
                }
            }, 1000);
            return ratio;
        },
        unfill() {
            armed = false;
            clearInterval(watchTimer);
            clear();
        },
        mark(text) {
            marker = text;
            applyMarker();
            titleObserver = new MutationObserver(applyMarker);
            titleObserver.observe(document.head || document.documentElement, {subtree: true, childList: true, characterData: true});
        },
        unmark() {
            if (titleObserver) titleObserver.disconnect();
            if (marker && document.title.startsWith(marker)) document.title = document.title.slice(marker.length);
            marker = null;
        },
    };
})();
