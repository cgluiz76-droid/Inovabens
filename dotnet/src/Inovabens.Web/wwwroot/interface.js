// Comportamentos de interface que não dependem do circuito do Blazor (funcionam também nas páginas estáticas).
// 1) Tema claro/escuro: botão [data-alternar-tema]; a escolha fica no navegador (localStorage "tema").
// 2) Modal acessível: ao abrir, o foco entra no modal; Tab circula só dentro dele; Esc fecha (clica no botão
//    "Fechar"); ao fechar, o foco volta para quem abriu. Vale para todo <div role="dialog" aria-modal="true">.
(function () {
    "use strict";
    var raiz = document.documentElement;

    function temaAtual() {
        var t = raiz.getAttribute("data-tema");
        if (t) return t;
        return window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches ? "escuro" : "claro";
    }

    document.addEventListener("click", function (e) {
        var botao = e.target.closest && e.target.closest("[data-alternar-tema]");
        if (!botao) return;
        var novo = temaAtual() === "escuro" ? "claro" : "escuro";
        raiz.setAttribute("data-tema", novo);
        try { localStorage.setItem("tema", novo); } catch (_) { }
        botao.setAttribute("aria-pressed", novo === "escuro" ? "true" : "false");
    });

    var FOCAVEIS = 'a[href], button:not([disabled]), input:not([disabled]):not([type=hidden]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';
    var pilha = []; // { modal, origem }

    function modais() { return Array.prototype.slice.call(document.querySelectorAll('[role="dialog"][aria-modal="true"]')); }
    function visiveis(el) { return Array.prototype.filter.call(el.querySelectorAll(FOCAVEIS), function (x) { return x.offsetParent !== null || x === document.activeElement; }); }

    function sincronizar() {
        var abertos = modais();
        // fechados: devolve o foco a quem abriu
        pilha = pilha.filter(function (item) {
            if (abertos.indexOf(item.modal) >= 0) return true;
            if (item.origem && document.contains(item.origem)) item.origem.focus();
            return false;
        });
        // novos: guarda a origem e leva o foco para dentro (primeiro campo; senão o próprio modal)
        abertos.forEach(function (m) {
            if (pilha.some(function (i) { return i.modal === m; })) return;
            pilha.push({ modal: m, origem: document.activeElement });
            if (!m.hasAttribute("tabindex")) m.setAttribute("tabindex", "-1");
            var campos = visiveis(m).filter(function (x) { return x.getAttribute("aria-label") !== "Fechar"; });
            (campos[0] || m).focus({ preventScroll: true });
        });
    }

    // o Blazor altera o DOM o tempo todo: agrupa as mudanças em uma verificação por quadro
    var agendado = false;
    new MutationObserver(function () {
        if (agendado) return;
        agendado = true;
        requestAnimationFrame(function () { agendado = false; sincronizar(); });
    }).observe(document.body, { childList: true, subtree: true });

    document.addEventListener("keydown", function (e) {
        var topo = pilha.length ? pilha[pilha.length - 1].modal : null;
        if (!topo) return;
        if (e.key === "Escape") {
            var fechar = topo.querySelector('[aria-label="Fechar"]');
            if (fechar) { e.preventDefault(); fechar.click(); }
            return;
        }
        if (e.key !== "Tab") return;
        var lista = visiveis(topo);
        if (!lista.length) { e.preventDefault(); topo.focus(); return; }
        var primeiro = lista[0], ultimo = lista[lista.length - 1];
        if (e.shiftKey && (document.activeElement === primeiro || document.activeElement === topo)) { e.preventDefault(); ultimo.focus(); }
        else if (!e.shiftKey && document.activeElement === ultimo) { e.preventDefault(); primeiro.focus(); }
        else if (!topo.contains(document.activeElement)) { e.preventDefault(); primeiro.focus(); }
    });
})();
