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
        if (botao) e.preventDefault();
        if (!botao) return;
        var novo = temaAtual() === "escuro" ? "claro" : "escuro";
        raiz.setAttribute("data-tema", novo);
        try { localStorage.setItem("tema", novo); } catch (_) { }
        botao.setAttribute("aria-pressed", novo === "escuro" ? "true" : "false");
    });

    // Menu lateral. Desktop: [data-recolher-menu] alterna só ícones (html[data-menu], guardado em localStorage "menu").
    // Celular: [data-abrir-menu] abre a gaveta (html[data-menu-aberto]); fecha no véu, no Esc ou ao escolher um item.
    var celular = window.matchMedia("(max-width: 860px)");
    function menuRecolhido() {
        var lateral = document.getElementById("menu-lateral");
        return !!lateral && lateral.getBoundingClientRect().width < 120;
    }
    function gaveta(abrir, voltarFoco) {
        var botao = document.querySelector("[data-abrir-menu]");
        if (abrir) raiz.setAttribute("data-menu-aberto", ""); else raiz.removeAttribute("data-menu-aberto");
        if (botao) botao.setAttribute("aria-expanded", abrir ? "true" : "false");
        if (abrir) {
            var primeiro = document.querySelector("#menu-lateral .nav-item");
            if (primeiro) primeiro.focus({ preventScroll: true });
        } else if (voltarFoco && botao) botao.focus();
    }
    document.addEventListener("click", function (e) {
        if (!e.target.closest) return;
        if (e.target.closest("[data-recolher-menu]")) {
            var novo = menuRecolhido() ? "aberto" : "recolhido";
            raiz.setAttribute("data-menu", novo);
            try { localStorage.setItem("menu", novo); } catch (_) { }
            return;
        }
        if (e.target.closest("[data-abrir-menu]")) { gaveta(!raiz.hasAttribute("data-menu-aberto"), false); return; }
        if (e.target.closest("[data-fechar-menu]")) { gaveta(false, true); return; }
        if (raiz.hasAttribute("data-menu-aberto") && e.target.closest("#menu-lateral a")) gaveta(false, false);
    });
    document.addEventListener("keydown", function (e) {
        if (e.key === "Escape" && raiz.hasAttribute("data-menu-aberto")) gaveta(false, true);
    });
    celular.addEventListener("change", function () { if (!celular.matches) gaveta(false, false); });

    // Busca global: Ctrl+K (⌘K no Mac) clica na lupa do topo; o Blazor abre a janela. Até o campo aparecer
    // (ida e volta ao servidor), as teclas digitadas ficam guardadas e são passadas para ele.
    var buscaPendente = null, buscaLimite = 0;
    function entregarBusca() {
        if (buscaPendente === null) return;
        var campo = document.querySelector(".busca-input");
        if (!campo) { if (Date.now() > buscaLimite) buscaPendente = null; return; }
        campo.focus({ preventScroll: true });
        if (buscaPendente) {
            campo.value = buscaPendente;
            campo.dispatchEvent(new Event("input", { bubbles: true }));
        }
        buscaPendente = null;
    }
    document.addEventListener("click", function (e) {
        if (e.target.closest && e.target.closest("[data-abrir-busca]")) { buscaPendente = ""; buscaLimite = Date.now() + 4000; }
    });
    document.addEventListener("keydown", function (e) {
        if (buscaPendente !== null && !document.querySelector(".busca-input")) {
            if (e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey) { buscaPendente += e.key; e.preventDefault(); }
            return;
        }
        if ((e.key !== "k" && e.key !== "K") || !(e.ctrlKey || e.metaKey) || e.altKey || e.shiftKey) return;
        var lupa = document.querySelector("[data-abrir-busca]");
        if (!lupa) return;
        e.preventDefault();
        if (!document.querySelector(".busca-global")) lupa.click();
    }, true);

    // CRM no celular: o seletor de etapas rola o quadro até a coluna; ao rolar o quadro, marca a etapa visível.
    document.addEventListener("click", function (e) {
        var chip = e.target.closest && e.target.closest("[data-ir-coluna]");
        if (!chip) return;
        var col = document.getElementById(chip.getAttribute("data-ir-coluna"));
        if (!col) return;
        var quadro = col.parentElement;
        var reduzir = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
        quadro.scrollTo({ left: col.offsetLeft - quadro.offsetLeft - parseFloat(getComputedStyle(quadro).paddingLeft || "0"), behavior: reduzir ? "auto" : "smooth" });
    });
    var etapaAgendada = false;
    function marcarEtapa() {
        etapaAgendada = false;
        var chips = document.querySelectorAll(".seletor-etapas [data-ir-coluna]");
        if (!chips.length || chips[0].offsetParent === null) return;
        var quadro = (document.getElementById(chips[0].getAttribute("data-ir-coluna")) || {}).parentElement;
        if (!quadro) return;
        var esquerda = quadro.getBoundingClientRect().left, melhor = null, menor = Infinity;
        for (var i = 0; i < chips.length; i++) {
            var col = document.getElementById(chips[i].getAttribute("data-ir-coluna"));
            if (!col) continue;
            var d = Math.abs(col.getBoundingClientRect().left - esquerda);
            if (d < menor) { menor = d; melhor = chips[i]; }
        }
        for (var j = 0; j < chips.length; j++) {
            var ativo = chips[j] === melhor;
            if ((chips[j].getAttribute("aria-current") === "true") !== ativo) chips[j].setAttribute("aria-current", ativo ? "true" : "false");
        }
        if (melhor) melhor.scrollIntoView({ block: "nearest", inline: "nearest" });
    }
    document.addEventListener("scroll", function (e) {
        if (etapaAgendada || !e.target.classList || !e.target.classList.contains("kanban")) return;
        etapaAgendada = true;
        requestAnimationFrame(marcarEtapa);
    }, true);

    var FOCAVEIS ='a[href], button:not([disabled]), input:not([disabled]):not([type=hidden]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';
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

    // 3) Tabelas → cartões no celular: tabelas com 4+ colunas ganham .tabela-cartoes e cada célula recebe o rótulo
    //    da coluna (data-label), usado pelo CSS abaixo de 640px. Sem mexer no HTML das telas.
    function rotularTabelas() {
        var tabelas = document.querySelectorAll(".tabela-container > .tabela, .tabela-rolagem > .tabela");
        for (var i = 0; i < tabelas.length; i++) {
            var t = tabelas[i];
            var cab = t.tHead && t.tHead.rows.length ? t.tHead.rows[t.tHead.rows.length - 1].cells : null;
            if (!cab || cab.length < 4) continue;
            var rotulos = [];
            for (var c = 0; c < cab.length; c++) {
                var r = (cab[c].textContent || "").trim();
                for (var s = 0; s < (cab[c].colSpan || 1); s++) rotulos.push(r);
            }
            if (!t.classList.contains("tabela-cartoes")) t.classList.add("tabela-cartoes");
            for (var b = 0; b < t.tBodies.length; b++) {
                var linhas = t.tBodies[b].rows;
                for (var l = 0; l < linhas.length; l++) {
                    var celulas = linhas[l].cells, pos = 0;
                    for (var k = 0; k < celulas.length; k++) {
                        var rot = celulas[k].colSpan > 1 ? "" : (rotulos[pos] || "");
                        if (celulas[k].getAttribute("data-label") !== rot) celulas[k].setAttribute("data-label", rot);
                        pos += celulas[k].colSpan || 1;
                    }
                }
            }
        }
    }

    // o Blazor altera o DOM o tempo todo: agrupa as mudanças em uma verificação por quadro
    //    Rotular tabelas percorre todas as células: só refaz quando a mudança tocou uma tabela (o CRM, por exemplo,
    //    muda o DOM a todo clique e não tem tabela nenhuma).
    var agendado = false, tabelasMudaram = true;
    function tocaTabela(no) {
        if (no.nodeType !== 1) no = no.parentNode;
        if (!no || no.nodeType !== 1) return false;
        return !!(no.closest("table") || no.querySelector("table"));
    }
    function aoMudar(registros) {
        if (!tabelasMudaram && registros) {
            for (var i = 0; i < registros.length && !tabelasMudaram; i++) {
                var r = registros[i];
                if (r.target.closest && r.target.closest("table")) { tabelasMudaram = true; break; }
                for (var a = 0; a < r.addedNodes.length; a++) if (tocaTabela(r.addedNodes[a])) { tabelasMudaram = true; break; }
            }
        }
        if (agendado) return;
        agendado = true;
        requestAnimationFrame(function () {
            agendado = false; sincronizar(); entregarBusca(); marcarEtapa();
            if (tabelasMudaram) { tabelasMudaram = false; rotularTabelas(); }
        });
    }
    new MutationObserver(aoMudar).observe(document.body, { childList: true, subtree: true });
    aoMudar();

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
