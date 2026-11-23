// GeradorPaginas: tela única. Fala com a API local (/api/...) e mostra o resultado numa pré-visualização sem scripts.
"use strict";

const $ = id => document.getElementById(id);
const estado = { resultado: null, aba: "html", controle: null };

// Todo POST leva X-Gerador: 1 (o servidor recusa POST sem ele: outro site não consegue usar a ferramenta)
async function api(caminho, corpo, sinal) {
    const r = await fetch("/api/" + caminho, corpo === undefined ? { signal: sinal } : {
        method: "POST", signal: sinal,
        headers: { "Content-Type": "application/json", "X-Gerador": "1" },
        body: JSON.stringify(corpo),
    });
    const dados = await r.json().catch(() => ({}));
    return { ok: r.ok, status: r.status, dados };
}

/* ------------------------------ login ------------------------------ */

function mostrarVerificacao(v) {
    const selo = $("situacao");
    selo.textContent = v.ok ? "Claude conectado (assinatura)" : "Login necessário";
    selo.className = "selo " + (v.ok ? "ok" : "falha");
    const aviso = $("aviso-chave");
    aviso.hidden = !v.aviso;
    aviso.textContent = v.aviso ? "⚠ " + v.aviso : "";
    const lista = $("itens-verificacao");
    lista.replaceChildren(...v.itens.map(i => {
        const li = document.createElement("li");
        li.className = i.ok ? "" : "falha";
        li.textContent = `${i.nome}: ${i.detalhe}`;
        return li;
    }));
    $("tela-login").hidden = v.ok;
    $("tela-principal").hidden = !v.ok;
}

async function verificar() {
    const b = $("verificar");
    b.disabled = true;
    $("situacao").textContent = "Verificando login…";
    $("situacao").className = "selo";
    try {
        const r = await api("status");
        mostrarVerificacao(r.dados);
    } catch {
        mostrarVerificacao({ ok: false, itens: [{ nome: "Ferramenta", ok: false, detalhe: "O servidor local não respondeu." }] });
    } finally { b.disabled = false; }
}

/* ------------------------------ geração ------------------------------ */

function opcoes() {
    return {
        paleta: $("paleta").value,
        prefixo: $("prefixo").value,
        formato: document.querySelector("input[name=formato]:checked").value,
        responsivo: $("responsivo").checked,
    };
}

async function executar(caminho, corpo, botao) {
    $("erro").hidden = true;
    estado.controle = new AbortController();
    const inicio = Date.now();
    const relogio = setInterval(() => { $("progresso").textContent = `Gerando… ${Math.round((Date.now() - inicio) / 1000)} s`; }, 500);
    document.querySelectorAll("#gerar, #refinar").forEach(b => b.disabled = true);
    $("cancelar").hidden = false;
    try {
        const r = await api(caminho, corpo, estado.controle.signal);
        if (r.status === 428) { mostrarVerificacao(r.dados.verificacao); return; }
        if (!r.ok) { mostrarErro(r.dados.erro || `Erro ${r.status}.`, r.dados.bruto); return; }
        mostrarResultado(r.dados);
        carregarHistorico();
    } catch (e) {
        mostrarErro(e.name === "AbortError" ? "Pedido cancelado." : "Falha ao falar com a ferramenta: " + e.message);
    } finally {
        clearInterval(relogio);
        $("progresso").textContent = "";
        document.querySelectorAll("#gerar, #refinar").forEach(b => b.disabled = false);
        $("cancelar").hidden = true;
    }
}

function mostrarErro(msg, bruto) {
    $("erro").textContent = msg;
    $("erro").hidden = false;
    mostrarBruto(bruto);
}

function mostrarBruto(bruto) {
    $("bruto-caixa").hidden = !bruto;
    $("bruto").textContent = bruto || "";
    if (bruto) $("bruto-caixa").open = true;
}

function documento(r) {
    return `<!doctype html>\n<html lang="pt-BR">\n<head>\n<meta charset="utf-8">\n` +
        `<meta name="viewport" content="width=device-width, initial-scale=1">\n<title>Página gerada</title>\n` +
        `<style>\nbody { margin: 0; font-family: system-ui, sans-serif; }\n${r.css}\n</style>\n</head>\n<body>\n${r.html}\n</body>\n</html>\n`;
}

function mostrarResultado(r) {
    estado.resultado = r;
    const aviso = $("aviso-resultado");
    aviso.hidden = !r.aviso;
    aviso.textContent = r.aviso ? "⚠ " + r.aviso : "";
    mostrarBruto(r.bruto);
    const temCodigo = !!r.html;
    $("vazio").hidden = temCodigo;
    $("conteudo").hidden = !temCodigo;
    if (!temCodigo) return;
    $("previa").srcdoc = documento(r);
    // o formato e o prefixo do resultado voltam para o formulário (para refinar com as mesmas regras)
    document.querySelector(`input[name=formato][value=${r.formato}]`).checked = true;
    $("prefixo").value = r.prefixo;
    trocarAba("html");
}

function trocarAba(aba) {
    estado.aba = aba;
    document.querySelectorAll(".aba").forEach(b => {
        const ativa = b.dataset.aba === aba;
        b.classList.toggle("ativa", ativa);
        b.setAttribute("aria-selected", ativa);
    });
    const r = estado.resultado;
    $("codigo").value = !r ? "" : aba === "html" ? r.htmlFinal : r.css;
}

function textoCopia(tipo) {
    const r = estado.resultado;
    if (tipo === "html") return r.htmlFinal;
    if (tipo === "css") return r.css;
    return r.css && r.formato !== "unico" ? `<style>\n${r.css}\n</style>\n${r.html}` : r.htmlFinal;
}

async function copiar(tipo) {
    const t = textoCopia(tipo);
    try {
        await navigator.clipboard.writeText(t);
        $("copiado").textContent = { html: "HTML copiado.", css: "CSS copiado.", tudo: "HTML e CSS copiados." }[tipo];
    } catch {
        $("codigo").value = t; $("codigo").select();
        $("copiado").textContent = "Selecionado: tecle Ctrl+C.";
    }
    setTimeout(() => $("copiado").textContent = "", 2500);
}

function baixar() {
    const r = estado.resultado;
    const a = document.createElement("a");
    a.href = URL.createObjectURL(new Blob([documento(r)], { type: "text/html;charset=utf-8" }));
    a.download = (r.imovel ? r.imovel.toLowerCase() + "-" : "") + (r.historico || "pagina") + ".html";
    a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 1000);
}

/* ------------------------------ histórico ------------------------------ */

async function carregarHistorico() {
    const r = await api("historico");
    const ul = $("historico");
    if (!r.ok || r.dados.length === 0) { ul.innerHTML = '<li class="mudo">Nada gerado ainda.</li>'; return; }
    ul.replaceChildren(...r.dados.map(h => {
        const li = document.createElement("li"), b = document.createElement("button"), d = document.createElement("b");
        b.type = "button";
        d.textContent = h.nome.replace("_", " ").replace(/(\d{2})(\d{2})(\d{2})/, "$1:$2:$3");
        b.append(d, document.createTextNode(h.resumo || "(sem descrição)"));
        b.onclick = () => abrirHistorico(h.nome);
        li.append(b);
        return li;
    }));
}

async function abrirHistorico(nome) {
    const r = await api("historico/" + encodeURIComponent(nome));
    if (!r.ok) { mostrarErro("Não foi possível abrir este item do histórico."); return; }
    const d = r.dados.dados;
    const final = d.formato === "unico" && d.css ? `<style>\n${d.css}\n</style>\n${d.html}` : d.html;
    $("erro").hidden = true;
    mostrarResultado({ html: d.html, css: d.css, htmlFinal: final, formato: d.formato, prefixo: d.prefixo, historico: nome, imovel: d.imovel });
}

/* ------------------------------ eventos ------------------------------ */

$("verificar").onclick = verificar;
$("form-gerar").onsubmit = e => {
    e.preventDefault();
    executar("gerar", { descricao: $("descricao").value, ...opcoes() });
};
$("form-refinar").onsubmit = e => {
    e.preventDefault();
    const r = estado.resultado;
    if (!r) return;
    executar("refinar", { html: r.html, css: r.css, instrucao: $("instrucao").value, ...opcoes() });
};
$("cancelar").onclick = () => estado.controle?.abort();
document.querySelectorAll(".aba").forEach(b => b.onclick = () => trocarAba(b.dataset.aba));
document.querySelectorAll("[data-copiar]").forEach(b => b.onclick = () => copiar(b.dataset.copiar));
$("baixar").onclick = baixar;
document.querySelectorAll("[data-largura]").forEach(b => b.onclick = () => {
    document.querySelectorAll("[data-largura]").forEach(x => x.classList.toggle("ativo", x === b));
    $("previa").style.width = b.dataset.largura ? b.dataset.largura + "px" : "100%";
});

verificar();
carregarHistorico();
