// Simulador de financiamento da ficha do imóvel no site (pré-visualização) com o comparativo dos bancos.
// Mesmo cálculo de FinanciamentoCaixa.Simular (C#): juros mensais = taxa nominal anual / 12; SAC com amortização
// constante, Price com parcela fixa. As taxas de cada banco (médias do Banco Central) e as faixas do Minha Casa,
// Minha Vida vêm do servidor em #sim-dados. Tudo é recalculado aqui, sem consultar o servidor.
(() => {
    const P = JSON.parse(document.getElementById("sim-dados").textContent);
    const $ = id => document.getElementById(id);
    const brl = v => v.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
    const pct = v => (v * 100).toLocaleString("pt-BR", { maximumFractionDigits: 2 }) + "%";
    const prazoTxt = m => `${Math.floor(m / 12)} anos${m % 12 ? ` e ${m % 12} meses` : ""} (${m} meses)`;
    let selecionado = P.padrao; // código do banco ou "mcmv"

    // Campos em reais no formato brasileiro (28.000.000,00): a digitação vira centavos ("2800000000" = 28.000.000,00)
    const reais = el => (+el.value.replace(/\D/g, "") || 0) / 100;
    const escreverReais = (el, v) => {
        el.value = v ? v.toLocaleString("pt-BR", { minimumFractionDigits: 2, maximumFractionDigits: 2 }) : "";
    };
    const mascarar = el => {
        const fim = el.value.length - (el.selectionEnd ?? el.value.length); // mantém o cursor a partir do fim
        escreverReais(el, reais(el));
        const pos = Math.max(0, el.value.length - fim);
        el.setSelectionRange(pos, pos);
    };

    /** Parcelas para o valor financiado p, n meses e juros nominais anuais. */
    const simular = (p, n, nominal, sac) => {
        const i = nominal / 12;
        if (sac) {
            const amort = p / n;
            return { primeira: amort + p * i, ultima: amort + amort * i, juros: p * i * (n + 1) / 2, efetiva: Math.pow(1 + i, 12) - 1 };
        }
        const pmt = i === 0 ? p / n : p * i / (1 - Math.pow(1 + i, -n));
        return { primeira: pmt, ultima: pmt, juros: pmt * n - p, efetiva: Math.pow(1 + i, 12) - 1 };
    };

    /** Faixa do MCMV: pela renda informada; sem renda, a melhor faixa em que o valor do imóvel cabe. */
    const faixaMcmv = (valor, renda) => {
        if (renda) {
            const f = P.mcmv.faixas.find(x => renda <= x.renda);
            if (!f) return { fora: `Renda acima do limite do programa (${brl(P.mcmv.faixas.at(-1).renda)}).` };
            if (valor > f.teto) return { fora: `Na faixa ${f.numero} (renda até ${brl(f.renda)}) o imóvel pode custar até ${brl(f.teto)}.` };
            return { f, pelaRenda: true };
        }
        const f = P.mcmv.faixas.find(x => valor <= x.teto);
        return f ? { f, pelaRenda: false } : null;
    };

    /** Logo oficial (wwwroot/bancos) num selo branco; o nome vai no alt. */
    const logo = (src, nome, extra = "") => {
        const s = document.createElement("span");
        s.className = "banco-logo" + extra;
        s.append(Object.assign(document.createElement("img"), { src, alt: nome }));
        return s;
    };

    const calcular = ev => {
        const alvo = ev?.target?.id;
        if (alvo === "simValor" || alvo === "simEntrada" || alvo === "simRenda") mascarar(ev.target);
        const sac = document.querySelector("input[name=sistema]:checked").value === "sac";
        const valor = Math.max(0, reais($("simValor")));
        const renda = reais($("simRenda"));
        const avisos = [];

        // ao mudar o valor do imóvel, a entrada volta a ser a padrão (20% do valor); depois pode ser alterada
        if (alvo === "simValor") escreverReais($("simEntrada"), Math.round(valor * P.entradaPadrao * 100) / 100);

        // entrada mínima pela cota do sistema (SAC 80%, Price 70%)
        const cota = sac ? P.cotaSac : P.cotaPrice;
        const entMin = Math.round(valor * (1 - cota) * 100) / 100;
        let entrada = Math.min(reais($("simEntrada")), valor);
        if (entrada < entMin) {
            entrada = entMin;
            if (alvo !== "simEntrada") escreverReais($("simEntrada"), entMin);
            avisos.push(`No ${sac ? "SAC" : "Price"} os bancos financiam até ${pct(cota)} do valor: a entrada mínima é de ${brl(entMin)}.`);
        }
        $("simEntradaPct").textContent = valor ? `${pct(entrada / valor)} do valor` : "";

        // idade + prazo até 80 anos e 6 meses
        const idade = +$("simIdade").value || 0;
        const prazoMax = idade ? Math.max(0, Math.min(P.prazoMax, P.idadeMaxMeses - idade * 12)) : P.prazoMax;
        const r = $("simPrazo");
        r.max = Math.max(12, prazoMax);
        if (+r.value > prazoMax) r.value = prazoMax;
        const semPrazo = idade && prazoMax < 12;
        if (semPrazo) avisos.push("Pela idade informada os bancos não financiam: idade mais prazo passaria de 80 anos e 6 meses.");
        else if (idade && prazoMax < P.prazoMax) avisos.push(`Com ${idade} anos o prazo máximo é de ${prazoTxt(prazoMax)}.`);
        const n = Math.max(1, Math.min(+r.value, prazoMax || 1));
        $("simPrazoTxt").textContent = prazoTxt(n);

        const p = Math.max(0, valor - entrada);
        const bancos = P.bancos.map(b => ({ ...b, ...simular(p, n, b.nominal, sac) }))
            .sort((a, b) => a.primeira - b.primeira || a.nominal - b.nominal);

        // faixa de cartões: do menor para o maior valor de parcela; o primeiro leva o selo
        const faixa = $("simBancos");
        faixa.replaceChildren(...bancos.map((b, k) => {
            const bt = document.createElement("button");
            bt.type = "button";
            bt.className = "banco-cartao";
            bt.setAttribute("role", "radio");
            bt.setAttribute("aria-checked", String(b.codigo === selecionado));
            bt.dataset.banco = b.codigo;
            const info = document.createElement("span");
            info.className = "banco-info";
            info.innerHTML = `<b class="num"></b><small></small>`;
            info.querySelector("b").textContent = semPrazo ? "—" : brl(b.primeira);
            info.querySelector("small").textContent = `${pct(b.taxaAno)} a.a. + TR`;
            bt.append(logo(b.logo, b.nome), info);
            if (k === 0 && !semPrazo) {
                const selo = document.createElement("span");
                selo.className = "banco-selo";
                selo.textContent = "Menor parcela";
                bt.prepend(selo);
            }
            bt.title = b.nome;
            bt.addEventListener("click", () => { selecionado = b.codigo; calcular(); });
            return bt;
        }));

        // cartão do Minha Casa, Minha Vida: fora da ordenação, só para imóveis dentro do teto do programa
        const mc = faixaMcmv(valor, renda);
        const cartaoMcmv = $("simMcmv");
        let mcmv = null;
        if (mc && valor <= P.mcmv.faixas.at(-1).teto) {
            cartaoMcmv.hidden = false;
            const bt = document.createElement("button");
            bt.type = "button";
            bt.className = "banco-cartao mcmv";
            bt.setAttribute("role", "radio");
            bt.setAttribute("aria-checked", String(selecionado === "mcmv"));
            bt.dataset.banco = "mcmv";
            const texto = document.createElement("span");
            texto.className = "banco-info";
            if (mc.f) {
                mcmv = { ...simular(p, n, mc.f.min, sac), f: mc.f, pelaRenda: mc.pelaRenda };
                texto.innerHTML = `<small class="mcmv-chamada">Você pode ter direito</small><b class="num"></b><small></small>`;
                texto.querySelector("b").textContent = `a partir de ${brl(mcmv.primeira)}`;
                texto.querySelector("small:last-child").textContent =
                    `Faixa ${mc.f.numero} · ${pct(mc.f.min)} a ${pct(mc.f.max)} a.a.${mc.pelaRenda ? "" : " · informe a renda"}`;
                bt.addEventListener("click", () => { selecionado = "mcmv"; calcular(); });
            } else {
                texto.innerHTML = `<small class="mcmv-chamada">Minha Casa, Minha Vida</small><small></small>`;
                texto.querySelector("small:last-child").textContent = mc.fora;
                bt.disabled = true;
            }
            bt.append(logo(P.mcmv.logo, "Minha Casa, Minha Vida", " mcmv"), texto);
            cartaoMcmv.replaceChildren(bt);
        } else {
            cartaoMcmv.hidden = true;
            cartaoMcmv.replaceChildren();
        }
        if (selecionado === "mcmv" && !mcmv) selecionado = P.padrao;

        // detalhe do escolhido
        const notas = [...avisos];
        let d, nome, link, rotuloTaxa;
        if (selecionado === "mcmv") {
            d = mcmv;
            nome = "Minha Casa, Minha Vida";
            link = P.mcmv.link;
            rotuloTaxa = `Faixa ${mcmv.f.numero} (renda familiar até ${brl(mcmv.f.renda)}, imóvel até ${brl(mcmv.f.teto)}): juros de ${pct(mcmv.f.min)} a ${pct(mcmv.f.max)} ao ano + TR`;
            $("dLogo").replaceWith(Object.assign(logo(P.mcmv.logo, "Minha Casa, Minha Vida", " mcmv"), { id: "dLogo" }));
            notas.push(`Calculado com a menor taxa da faixa (${pct(mcmv.f.min)}); a taxa varia com a região e com o tempo de FGTS.`);
            if (!mcmv.pelaRenda) notas.push("Informe a renda familiar para o sistema identificar a sua faixa.");
            notas.push("É preciso não ter outro imóvel nem financiamento habitacional. Nas faixas 1 e 2 pode haver subsídio na entrada, que esta simulação não considera.");
        } else {
            const b = bancos.find(x => x.codigo === selecionado) ?? bancos[0];
            d = b;
            nome = b.nome;
            link = b.link;
            rotuloTaxa = `Taxa média de ${pct(b.taxaAno)} ao ano + TR (Banco Central, ${b.mes})`;
            $("dLogo").replaceWith(Object.assign(logo(b.logo, b.nome), { id: "dLogo" }));
            notas.push(`Taxa média que o banco praticou em ${b.mes}, segundo o Banco Central; a sua depende do relacionamento com o banco e da análise de crédito.`);
            if (b.cooperativa) notas.push("Cooperativa de crédito: para contratar é preciso se associar a uma cooperativa da sua região.");
        }
        $("dNome").textContent = nome;
        $("dTaxa").textContent = rotuloTaxa;
        $("dLink").href = link;
        $("dLink").textContent = selecionado === "mcmv" || selecionado === "caixa" ? "Simular no site da Caixa ↗" : `Simular no site do ${nome} ↗`;

        $("rFinanciado").textContent = brl(p);
        $("rPrimeiraRotulo").textContent = sac ? "1ª parcela" : "Parcela fixa";
        $("rPrimeira").textContent = semPrazo ? "—" : brl(d.primeira);
        $("rUltimaCaixa").hidden = !sac;
        $("rUltima").textContent = semPrazo ? "—" : brl(d.ultima);
        $("rRenda").textContent = brl(d.primeira / P.comprometimento);
        $("rJuros").textContent = brl(d.juros);
        $("rEfetiva").textContent = `${pct(d.efetiva)} a.a.`;
        if (renda && d.primeira > renda * P.comprometimento)
            notas.unshift(`A parcela passa de ${pct(P.comprometimento)} da renda informada: aumente a entrada ou o prazo.`);
        $("simNotas").replaceChildren(...notas.map(t => Object.assign(document.createElement("li"), { textContent: t })));
    };

    $("simForm").addEventListener("input", calcular);
    $("simEntrada").addEventListener("change", () => calcular());
    escreverReais($("simValor"), +$("simValor").dataset.valor || 0);
    escreverReais($("simEntrada"), +$("simEntrada").dataset.valor || 0);
    calcular();
})();
