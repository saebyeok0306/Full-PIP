// 동작 순서
//   켜기: 지금 Chrome 창의 실제 위치를 도우미에게 받아 둔다 → 가장 큰 동영상을 탭 안에서 꽉 채운다
//        → 탭을 팝업 창으로 옮긴다 → 도우미가 원래 창 자리에 테두리 없는 액자 창을 만들고,
//          팝업 창의 웹 화면 부분만 보이도록 그 안에 넣는다.
//   끄기: 도우미가 팝업 창을 액자에서 꺼낸다 → 동영상을 원래대로 돌리고 탭을 원래 창에 되돌린다.
//        (탭이 빠진 팝업 창은 자동으로 닫힌다)
//   Esc 를 누르거나 액자 창을 닫아도(Alt+F4) 꺼진다.

const HOST = "com.areafullscreen.host";
const TOPMOST = false;  // true 면 영상 창이 항상 다른 창 위에 뜬다.
const INITIAL_AREA = 0.25;  // 영상 창의 처음 크기: 모니터 면적 대비 비율 (1 이면 원래 Chrome 창 크기 그대로)
const TITLE = "Full PIP 켜기/끄기 (Alt+Shift+F)";

const busy = new Set();

chrome.action.onClicked.addListener(tab => toggle(tab));
chrome.commands.onCommand.addListener((command, tab) => {
    if (command === "toggle" && tab) toggle(tab);
});
chrome.runtime.onMessage.addListener((msg, sender) => {
    if (msg.cmd === "exit" && sender.tab) toggle(sender.tab, true);
});
// 영상 창 안에서 페이지가 새로 로드되면 다시 꽉 채운다.
chrome.tabs.onUpdated.addListener(async (tabId, info) => {
    if (info.status === "complete" && await getState(tabId)) refill(tabId);
});
chrome.tabs.onRemoved.addListener(async tabId => {
    let state = await getState(tabId);
    if (!state) return;
    await setState(tabId, null);
    await native({cmd: "release", key: state.key});
    disconnectIfIdle();
});

async function toggle(tab, exitOnly = false) {
    if (busy.has(tab.id)) return;
    busy.add(tab.id);
    try {
        if (await getState(tab.id)) await exit(tab.id);
        else if (!exitOnly) await enter(tab);
    } catch (e) {
        console.error("[Full PIP]", e);
    } finally {
        busy.delete(tab.id);
    }
}

async function enter(tab) {
    // 단축키를 누른 지금 앞에 있는 Chrome 창의 보이는 영역(실제 픽셀)을 받아 둔다.
    let foreground = await native({cmd: "foreground"});
    if (!foreground.ok) {
        showError("도우미 프로그램 오류: " + foreground.error);
        disconnectIfIdle();
        return;
    }
    let ratio = await fillBestVideo(tab.id);
    if (!ratio) {
        disconnectIfIdle();
        return;
    }

    let win = await chrome.windows.get(tab.windowId);
    // 도우미가 팝업 창을 찾을 수 있도록 잠시 제목에 표시(key)를 붙인다.
    let key = "[AF-" + crypto.randomUUID().slice(0, 8) + "]";
    await setState(tab.id, {
        key,
        windowId: win.id,
        index: tab.index,
        bounds: {left: win.left, top: win.top, width: win.width, height: win.height},
        maximized: win.state === "maximized",
    });

    await call(tab.id, {frameIds: [0]}, "mark", key);
    await chrome.windows.create({
        tabId: tab.id, type: "popup", focused: true,
        left: win.left, top: win.top, width: win.width, height: win.height,
    });
    let result = await native({
        cmd: "embed", key, rect: foreground.rect, area: INITIAL_AREA, ratio, topmost: TOPMOST, title: tab.title,
    });
    await call(tab.id, {frameIds: [0]}, "unmark");

    if (result.ok) clearError();
    else showError("도우미 프로그램 오류: " + result.error);
}

async function exit(tabId) {
    let state = await getState(tabId);
    if (!state) return;
    await setState(tabId, null);
    await native({cmd: "release", key: state.key});
    await call(tabId, {allFrames: true}, "unfill").catch(() => {});
    await blockFullscreen(tabId, false);

    let windowId = state.windowId;
    try {
        await chrome.tabs.move(tabId, {windowId, index: state.index});
    } catch (e) {
        // 탭이 하나뿐이던 원래 창은 탭이 빠지면서 닫혔으므로 같은 자리에 새로 만든다.
        let win = await chrome.windows.create({tabId, type: "normal", ...state.bounds});
        windowId = win.id;
        if (state.maximized) await chrome.windows.update(windowId, {state: "maximized"});
    }
    await chrome.tabs.update(tabId, {active: true});
    await chrome.windows.update(windowId, {focused: true});
    disconnectIfIdle();
}

// 사용자가 액자 창을 닫으면(Alt+F4 등) 끄기와 같이 처리한다.
async function onFrameClosed(key) {
    let all = await chrome.storage.session.get(null);
    for (let [name, state] of Object.entries(all)) {
        if (name.startsWith("tab:") && state.key === key) {
            let tabId = Number(name.slice(4));
            chrome.tabs.get(tabId).then(tab => toggle(tab, true), () => setState(tabId, null));
        }
    }
}

async function refill(tabId) {
    // 새로 로드된 페이지는 동영상이 늦게 생길 수 있어 몇 번 다시 시도한다.
    for (let i = 0; i < 10; i++) {
        if (!await getState(tabId)) return;
        if (await fillBestVideo(tabId).catch(() => false)) return;
        await new Promise(r => setTimeout(r, 1000));
    }
}

// 모든 프레임(iframe 포함)에서 가장 큰 동영상을 찾아 그 프레임에서 꽉 채우고, 동영상의 가로/세로 비율을 돌려준다.
async function fillBestVideo(tabId) {
    await chrome.scripting.executeScript({target: {tabId, allFrames: true}, files: ["content.js"]});
    let scans = await call(tabId, {allFrames: true}, "scan");
    let best = scans.filter(r => r.result > 0).sort((a, b) => b.result - a.result)[0];
    if (!best) return false;
    await call(tabId, {allFrames: true}, "arm");
    let [filled] = await call(tabId, {frameIds: [best.frameId]}, "fill");
    await blockFullscreen(tabId, true);
    return filled && filled.result ? filled.result : 16 / 9;
}

// 사이트의 전체화면 버튼/단축키(f, 더블클릭 등)는 영상 창을 모니터 전체로 끌어내므로,
// 영상 창으로 보는 동안에는 페이지의 전체화면 요청을 아무 일도 하지 않게 바꿔 둔다.
function blockFullscreen(tabId, block) {
    return chrome.scripting.executeScript({
        target: {tabId, allFrames: true},
        world: "MAIN",
        args: [block],
        func: block => {
            const element = Element.prototype, video = HTMLVideoElement.prototype;
            const saved = window.__areaFullscreenSaved;
            if (block && !saved) {
                window.__areaFullscreenSaved = {
                    request: element.requestFullscreen,
                    webkitRequest: element.webkitRequestFullscreen,
                    webkitEnter: video.webkitEnterFullscreen,
                };
                element.requestFullscreen = () => Promise.resolve();
                element.webkitRequestFullscreen = () => {};
                video.webkitEnterFullscreen = () => {};
            } else if (!block && saved) {
                element.requestFullscreen = saved.request;
                element.webkitRequestFullscreen = saved.webkitRequest;
                video.webkitEnterFullscreen = saved.webkitEnter;
                delete window.__areaFullscreenSaved;
            }
        },
    }).catch(() => {});
}

// content.js 가 각 프레임에 만들어 둔 window.__areaFullscreen 의 함수를 호출한다.
function call(tabId, target, name, ...args) {
    return chrome.scripting.executeScript({
        target: {tabId, ...target},
        func: (name, args) => window.__areaFullscreen ? window.__areaFullscreen[name](...args) : null,
        args: [name, args],
    });
}

// 도우미 프로그램은 액자 창을 유지해야 하므로 연결(port)을 열어 둔 채 사용한다.
// 연결이 열려 있는 동안에는 이 서비스 워커도 종료되지 않는다.
let port = null;
let nextId = 1;
const pending = new Map();

function connect() {
    if (port) return port;
    port = chrome.runtime.connectNative(HOST);
    port.onMessage.addListener(msg => {
        if (msg.id && pending.has(msg.id)) {
            pending.get(msg.id)(msg);
            pending.delete(msg.id);
        } else if (msg.event === "closed") {
            onFrameClosed(msg.key);
        }
    });
    port.onDisconnect.addListener(() => {
        let error = chrome.runtime.lastError ? chrome.runtime.lastError.message : "연결 끊김";
        port = null;
        for (let resolve of pending.values()) resolve({ok: false, error: "설치되지 않았거나 실행할 수 없음 (" + error + ")"});
        pending.clear();
    });
    return port;
}

function native(msg) {
    return new Promise(resolve => {
        let id = nextId++;
        pending.set(id, resolve);
        try {
            connect().postMessage({...msg, id});
        } catch (e) {
            pending.delete(id);
            resolve({ok: false, error: e.message});
        }
    });
}

// 켜져 있는 영상 창이 없으면 도우미를 종료시킨다.
async function disconnectIfIdle() {
    let all = await chrome.storage.session.get(null);
    if (port && !Object.keys(all).some(name => name.startsWith("tab:"))) {
        port.disconnect();
        port = null;
    }
}

function showError(text) {
    console.warn("[Full PIP]", text);
    chrome.action.setBadgeText({text: "!"});
    chrome.action.setTitle({title: text});
}

function clearError() {
    chrome.action.setBadgeText({text: ""});
    chrome.action.setTitle({title: TITLE});
}

async function getState(tabId) {
    let key = "tab:" + tabId;
    return (await chrome.storage.session.get(key))[key];
}

function setState(tabId, state) {
    let key = "tab:" + tabId;
    return state ? chrome.storage.session.set({[key]: state}) : chrome.storage.session.remove(key);
}
