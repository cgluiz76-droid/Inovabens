// Máscaras de digitação para campos com data-mascara="cpf" | "cnpj" | "telefone" | "cep" (mesmas regras de Formatos.cs).
// Um único ouvinte no documento: vale para campos criados depois pelo Blazor. Na fase de captura, roda antes dos
// eventos do Blazor, que já recebem o valor formatado (no "input" e no "change").
(() => {
    const regras = {
        // 000.000.000-00
        cpf: { limpar: v => v.replace(/\D/g, '').slice(0, 11), modelo: () => '###.###.###-##' },
        // AA.AAA.AAA/AAAA-00: CNPJ alfanumérico (letras só nas 12 primeiras posições, verificadores numéricos)
        cnpj: {
            limpar: v => {
                const c = v.toUpperCase().replace(/[^A-Z0-9]/g, '');
                return (c.slice(0, 12) + c.slice(12).replace(/\D/g, '')).slice(0, 14);
            },
            modelo: () => '##.###.###/####-##',
        },
        // 00000-000
        cep: { limpar: v => v.replace(/\D/g, '').slice(0, 8), modelo: () => '#####-###' },
        // (11) 3456-7890 enquanto houver até 10 dígitos; (11) 98765-4321 com 11
        telefone: {
            limpar: v => v.replace(/\D/g, '').replace(/^55(?=\d{10,11}$)/, '').slice(0, 11),
            modelo: c => c.length > 10 ? '(##) #####-####' : '(##) ####-####',
        },
    };

    const aplicar = (c, modelo) => {
        let r = '', i = 0;
        for (const m of modelo) {
            if (i >= c.length) break;
            r += m === '#' ? c[i++] : m;
        }
        return r;
    };

    document.addEventListener('input', e => {
        const el = e.target;
        const regra = el instanceof HTMLInputElement && regras[el.dataset.mascara];
        if (!regra) return;
        // mantém o cursor depois do mesmo número de caracteres significativos
        const significativos = regra.limpar(el.value.slice(0, el.selectionStart ?? el.value.length)).length;
        const c = regra.limpar(el.value);
        const novo = aplicar(c, regra.modelo(c));
        if (novo === el.value) return;
        el.value = novo;
        let pos = 0, vistos = 0;
        while (pos < novo.length && vistos < significativos) if (/[A-Za-z0-9]/.test(novo[pos++])) vistos++;
        el.setSelectionRange(pos, pos);
    }, true);

    // Enter em campo de busca (data-sem-enviar) escolhe a opção destacada em vez de enviar o formulário
    document.addEventListener('keydown', e => {
        if (e.key === 'Enter' && e.target instanceof HTMLInputElement && 'semEnviar' in e.target.dataset) e.preventDefault();
    }, true);
})();
