// Simulador de financiamento da ficha do imóvel no site (pré-visualização).
// Mesmo cálculo de FinanciamentoCaixa.Simular (C#); os parâmetros da Caixa vêm do servidor em #sim-dados.
(() => {
    const P = JSON.parse(document.getElementById("sim-dados").textContent);
    const $ = id => document.getElementById(id);
    const brl = v => v.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
    const pct = v => (v * 100).toLocaleString("pt-BR", { maximumFractionDigits: 2 }) + "%";
    const prazoTxt = m => `${Math.floor(m / 12)} anos${m % 12 ? ` e ${m % 12} meses` : ""} (${m} meses)`;
    let taxaEditada = false;

    const calcular = ev => {
        const sac = document.querySelector("input[name=sistema]:checked").value === "sac";
        const valor = Math.max(0, +$("simValor").value || 0);
        const sfh = valor <= P.tetoSfh;
        if (!taxaEditada) $("simTaxa").value = ((sfh ? P.taxaSfh : P.taxaSfi) * 100).toFixed(2);
        const avisos = [];

        // entrada mínima pela cota do sistema (SAC 80%, Price 70%)
        const cota = sac ? P.cotaSac : P.cotaPrice;
        const entMin = Math.round(valor * (1 - cota) * 100) / 100;
        let entrada = Math.min(+$("simEntrada").value || 0, valor);
        if (entrada < entMin) {
            entrada = entMin;
            if (!ev || ev.target.id !== "simEntrada") $("simEntrada").value = Math.ceil(entMin);
            avisos.push(`No ${sac ? "SAC" : "Price"} a Caixa financia até ${pct(cota)} do valor: a entrada mínima é de ${brl(entMin)}.`);
        }
        $("simEntradaPct").textContent = valor ? `${pct(entrada / valor)} do valor` : "";

        // idade + prazo até 80 anos e 6 meses
        const idade = +$("simIdade").value || 0;
        const prazoMax = idade ? Math.max(0, Math.min(P.prazoMax, P.idadeMaxMeses - idade * 12)) : P.prazoMax;
        const r = $("simPrazo");
        r.max = Math.max(12, prazoMax);
        if (+r.value > prazoMax) r.value = prazoMax;
        if (idade && prazoMax < 12) avisos.push("Pela idade informada a Caixa não financia: idade mais prazo passaria de 80 anos e 6 meses.");
        else if (idade && prazoMax < P.prazoMax) avisos.push(`Com ${idade} anos o prazo máximo é de ${prazoTxt(prazoMax)}.`);
        const n = Math.max(1, Math.min(+r.value, prazoMax || 1));
        $("simPrazoTxt").textContent = prazoTxt(n);

        const p = Math.max(0, valor - entrada);
        const i = (+$("simTaxa").value || 0) / 100 / 12; // Caixa: taxa nominal anual / 12
        let primeira, ultima, juros;
        if (sac) {
            const amort = p / n;
            primeira = amort + p * i;
            ultima = amort + amort * i;
            juros = p * i * (n + 1) / 2;
        } else {
            primeira = ultima = i === 0 ? p / n : p * i / (1 - Math.pow(1 + i, -n));
            juros = primeira * n - p;
        }
        $("rFinanciado").textContent = brl(p);
        $("rPrimeiraRotulo").textContent = sac ? "1ª parcela" : "Parcela fixa";
        $("rPrimeira").textContent = brl(primeira);
        $("rUltimaCaixa").hidden = !sac;
        $("rUltima").textContent = brl(ultima);
        $("rRenda").textContent = brl(primeira / P.comprometimento);
        $("rJuros").textContent = brl(juros);
        $("rEfetiva").textContent = `${pct(Math.pow(1 + i, 12) - 1)} a.a.`;

        const notas = [...avisos, sfh
            ? `Taxa de balcão da Caixa no SFH; clientes com relacionamento (conta, salário, débito automático) podem ter a partir de ${pct(P.taxaSfhMin)} ao ano + TR.`
            : `Imóvel acima de ${brl(P.tetoSfh).replace(",00", "")}: financiamento pelo SFI, com juros a partir de ${pct(P.taxaSfiMin)} ao ano + TR.`];
        if (valor <= P.tetoSociais) notas.push("Conforme a renda e o uso do FGTS, o imóvel pode se enquadrar no Minha Casa, Minha Vida ou no Pró-Cotista, com juros menores.");
        $("simNotas").replaceChildren(...notas.map(t => Object.assign(document.createElement("li"), { textContent: t })));
    };

    $("simTaxa").addEventListener("input", () => { taxaEditada = true; });
    $("simForm").addEventListener("input", calcular);
    $("simEntrada").addEventListener("change", () => calcular());
    calcular();
})();
