// Mapa de imóveis em tela cheia (menu Imóveis → Mapa), com o Google Maps JavaScript API.
// Base "Satélite" = hybrid (satélite com nomes de ruas); base "Ruas" = roadmap. Supercluster agrupa alfinetes próximos.
// Cada imóvel é um alfinete HTML (referência + preço) numa camada própria (OverlayView, sem precisar de Map ID);
// passar o mouse mostra o resumo, clicar abre a página do imóvel no site.

const SUPERCLUSTER = "https://cdn.jsdelivr.net/npm/supercluster@8.0.1/dist/supercluster.min.js";
const ZOOM_MAXIMO = 20;

/** Sem filtro: São José do Rio Preto, raio de ~20 km (o usuário amplia depois). */
const RIO_PRETO = { lat: -20.8197, lng: -49.3794, raioKm: 20 };

const mapas = new Map();

function script(src) {
    return new Promise((ok, erro) => {
        const s = document.createElement("script");
        s.src = src; s.async = true; s.onload = ok;
        s.onerror = () => erro(new Error("Não foi possível carregar o mapa do Google. Confira a conexão."));
        document.head.appendChild(s);
    });
}

let carregando;
function carregarBibliotecas(chave) {
    if (window.google?.maps?.OverlayView && window.Supercluster) return Promise.resolve();
    carregando ??= (async () => {
        if (!window.Supercluster) await script(SUPERCLUSTER);
        if (!window.google?.maps?.OverlayView) {
            const pronto = new Promise((ok) => { window.__inovabensMapaPronto = ok; });
            // chave recusada (restrição de site, API não ativada, sem faturamento): o Google chama esta função
            window.gm_authFailure = () => {
                for (const e of mapas.values())
                    e.dotnet.invokeMethodAsync("MapaFalhou", "O Google recusou a chave do mapa. Confira em Administração → Configurações → Google Maps (Maps JavaScript API ativada e o endereço do sistema liberado na chave).").catch(() => { });
            };
            await script("https://maps.googleapis.com/maps/api/js?key=" + encodeURIComponent(chave)
                + "&v=weekly&language=pt-BR&region=BR&loading=async&callback=__inovabensMapaPronto");
            await pronto;
        }
    })().catch((e) => { carregando = null; throw e; });
    return carregando;
}

const esc = (t) => String(t ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

function precoCurto(v) {
    if (v == null) return "Consulte";
    if (v >= 1e6) return "R$ " + (v / 1e6).toLocaleString("pt-BR", { maximumFractionDigits: 2 }) + " mi";
    if (v >= 1e3) return "R$ " + Math.round(v / 1e3).toLocaleString("pt-BR") + " mil";
    return "R$ " + Math.round(v).toLocaleString("pt-BR");
}

/**
 * Centro de Rio Preto com o raio de 20 km inteiro na tela: zoom calculado pelo tamanho do elemento (fitBounds num mapa
 * que ainda não tem tamanho cai num zoom de mundo inteiro).
 */
function focarRioPreto(mapa, elemento) {
    const lado = Math.min(elemento.clientWidth || 800, elemento.clientHeight || 600);
    const metrosPorPixel = (2 * RIO_PRETO.raioKm * 1000) / Math.max(lado - 40, 200);
    const zoom = Math.log2(156543.03392 * Math.cos(RIO_PRETO.lat * Math.PI / 180) / metrosPorPixel);
    mapa.setCenter({ lat: RIO_PRETO.lat, lng: RIO_PRETO.lng });
    mapa.setZoom(Math.max(8, Math.min(14, Math.floor(zoom))));
}

/** Camada com os alfinetes e o resumo em HTML, posicionados pela projeção do mapa a cada movimento. */
function criarCamada() {
    const g = window.google.maps;
    return new (class extends g.OverlayView {
        constructor() {
            super();
            this.marcas = [];
            this.div = document.createElement("div");
            this.div.className = "camada-alfinetes";
            g.OverlayView.preventMapHitsAndGesturesFrom(this.div);
        }
        onAdd() { this.getPanes().overlayMouseTarget.appendChild(this.div); }
        onRemove() { this.div.remove(); }
        draw() {
            const proj = this.getProjection();
            if (!proj) return;
            for (const m of this.marcas) {
                const p = proj.fromLatLngToDivPixel(m.pos);
                if (p) m.el.style.transform = `translate(${Math.round(p.x)}px, ${Math.round(p.y)}px)`;
            }
        }
        /** marcas: [{ chave, el, pos }] — mantém os elementos que continuam e troca o resto. */
        definir(marcas) {
            const novas = new Set(marcas.map((m) => m.el));
            for (const m of this.marcas) if (!novas.has(m.el)) m.el.remove();
            for (const m of marcas) if (m.el.parentNode !== this.div) this.div.appendChild(m.el);
            this.marcas = marcas;
            this.draw();
        }
    })();
}

export async function iniciar(elemento, dotnet, chave) {
    await carregarBibliotecas(chave);
    const g = window.google.maps;
    const mapa = new g.Map(elemento, {
        mapTypeId: "hybrid",
        maxZoom: ZOOM_MAXIMO,
        gestureHandling: "greedy",
        clickableIcons: false,
        disableDefaultUI: true,
        zoomControl: true,
        zoomControlOptions: { position: g.ControlPosition.RIGHT_BOTTOM },
        scaleControl: true,
        tilt: 0,
    });
    focarRioPreto(mapa, elemento);

    const camada = criarCamada();
    camada.setMap(mapa);
    const estado = {
        mapa, dotnet, camada, itens: [], porId: new Map(), indice: null, elementos: new Map(), resumo: null,
        lista: new g.InfoWindow({ maxWidth: 300 }),
    };
    estado.ouvinte = mapa.addListener("idle", () => atualizar(estado));
    mapa.addListener("click", () => estado.lista.close());
    estado.tecla = (e) => { if (e.key === "Escape") dotnet.invokeMethodAsync("Fechar").catch(() => { }); };
    document.addEventListener("keydown", estado.tecla);
    mapas.set(elemento, estado);
}

/** Troca a base (Satélite / Ruas). */
export function base(elemento, nome) {
    mapas.get(elemento)?.mapa.setMapTypeId(nome === "ruas" ? "roadmap" : "hybrid");
}

/**
 * Recebe os imóveis com coordenadas. focoPadrao = ao abrir o mapa ou sem filtro ativo: centro de Rio Preto, raio de
 * 20 km (os alfinetes de fora aparecem ao afastar); com filtro, enquadra todos os resultados.
 */
export function desenhar(elemento, itens, focoPadrao) {
    const estado = mapas.get(elemento);
    if (!estado) return;
    estado.itens = itens;
    estado.porId = new Map(itens.map((i) => [i.id, i]));
    estado.indice = new window.Supercluster({ radius: 56, maxZoom: ZOOM_MAXIMO }).load(itens.map((i) => ({
        type: "Feature", geometry: { type: "Point", coordinates: [i.lng, i.lat] }, properties: { id: i.id },
    })));
    estado.elementos.clear();
    esconderResumo(estado);
    estado.lista.close();

    if (focoPadrao || itens.length === 0) {
        focarRioPreto(estado.mapa, elemento);
    } else if (itens.length === 1) {
        estado.mapa.setCenter({ lat: itens[0].lat, lng: itens[0].lng });
        estado.mapa.setZoom(16);
    } else {
        const lngs = itens.map((i) => i.lng), lats = itens.map((i) => i.lat);
        estado.mapa.fitBounds({ south: Math.min(...lats), north: Math.max(...lats), west: Math.min(...lngs), east: Math.max(...lngs) },
            { top: 90, bottom: 60, left: 60, right: 60 });
        limitarZoom(estado.mapa, 16);
    }
    atualizar(estado);
}

/** Depois de enquadrar, não aproxima além de z (um grupo pequeno não vira um close no telhado). */
function limitarZoom(mapa, z) {
    window.google.maps.event.addListenerOnce(mapa, "idle", () => { if (mapa.getZoom() > z) mapa.setZoom(z); });
}

/** Recalcula grupos e alfinetes para o trecho visível (reaproveita os elementos que continuam). */
function atualizar(estado) {
    const b = estado.mapa.getBounds();
    if (!estado.indice || !b) return;
    const sw = b.getSouthWest(), ne = b.getNorthEast();
    const zoom = Math.floor(estado.mapa.getZoom());
    const grupos = estado.indice.getClusters([sw.lng() - 0.05, sw.lat() - 0.05, ne.lng() + 0.05, ne.lat() + 0.05], zoom);
    const marcas = [];
    const usados = new Map();
    for (const gr of grupos) {
        const p = gr.properties;
        const chave = p.cluster ? "g" + p.cluster_id + "_" + zoom : "i" + p.id;
        const el = estado.elementos.get(chave)
            ?? (p.cluster ? elementoGrupo(estado, gr) : elementoAlfinete(estado, estado.porId.get(p.id)));
        usados.set(chave, el);
        const [lng, lat] = gr.geometry.coordinates;
        marcas.push({ chave, el, pos: new window.google.maps.LatLng(lat, lng) });
    }
    if (estado.resumo) marcas.push(estado.resumo);
    estado.elementos = usados;
    estado.camada.definir(marcas);
    // avisa a tela quantos estão no trecho visível
    const n = estado.itens.filter((i) => b.contains({ lat: i.lat, lng: i.lng })).length;
    estado.dotnet.invokeMethodAsync("AreaMudou", n).catch(() => { });
}

function elementoAlfinete(estado, i) {
    const pos = document.createElement("div");
    pos.className = "posicao-alfinete";
    const el = document.createElement("button");
    el.type = "button";
    el.className = "alfinete " + (i.cor || "");
    el.setAttribute("aria-label", `${i.rotulo} — ${i.titulo} — abrir no site`);
    el.innerHTML = `<span class="alfinete-etiqueta"><b>${esc(i.rotulo)}</b><span>${esc(precoCurto(i.preco))}</span></span>`
        + `<svg class="alfinete-icone" viewBox="0 0 24 34" aria-hidden="true"><path d="M12 0C5.4 0 0 5.3 0 11.9 0 20.8 12 34 12 34s12-13.2 12-22.1C24 5.3 18.6 0 12 0z"/><circle cx="12" cy="12" r="4.6"/></svg>`;
    el.addEventListener("mouseenter", () => mostrarResumo(estado, i));
    el.addEventListener("mouseleave", () => esconderResumo(estado));
    el.addEventListener("click", (e) => { e.stopPropagation(); window.open(i.site, "_blank", "noopener"); });
    pos.appendChild(el);
    return pos;
}

function elementoGrupo(estado, gr) {
    const n = gr.properties.point_count;
    const pos = document.createElement("div");
    pos.className = "posicao-grupo";
    const el = document.createElement("button");
    el.type = "button";
    el.className = "grupo-alfinetes";
    const lado = n < 10 ? 38 : n < 50 ? 46 : 54;
    el.style.width = el.style.height = lado + "px";
    el.textContent = n;
    el.setAttribute("aria-label", n + " imóveis — aproximar");
    el.addEventListener("click", (e) => {
        e.stopPropagation();
        const id = gr.properties.cluster_id;
        const zoom = estado.indice.getClusterExpansionZoom(id);
        const [lng, lat] = gr.geometry.coordinates;
        if (zoom > ZOOM_MAXIMO) {
            // mesmo ponto (ex.: lotes no mesmo endereço): lista para escolher
            const folhas = estado.indice.getLeaves(id, Infinity).map((f) => estado.porId.get(f.properties.id));
            const div = document.createElement("div");
            div.className = "lista-imoveis";
            div.innerHTML = `<div class="lista-titulo">${n} imóveis neste ponto</div>` + folhas.map((i) =>
                `<a href="${esc(i.site)}" target="_blank" rel="noopener">${i.capa ? `<img src="${esc(i.capa)}" alt="">` : "<span class=\"sem-capa\"></span>"}`
                + `<span><b>${esc(i.rotulo)}</b> · ${esc(precoCurto(i.preco))}<br><small>${esc(i.titulo)}</small></span></a>`).join("");
            estado.lista.setContent(div);
            estado.lista.setPosition({ lat, lng });
            estado.lista.open({ map: estado.mapa });
        } else {
            estado.mapa.panTo({ lat, lng });
            estado.mapa.setZoom(zoom);
        }
    });
    pos.appendChild(el);
    return pos;
}

function mostrarResumo(estado, i) {
    const el = document.createElement("div");
    el.className = "posicao-resumo";
    const capa = i.capa ? `<img src="${esc(i.capa)}" alt="" loading="lazy">` : "";
    el.innerHTML = `<div class="resumo-imovel">${capa}<div class="resumo-corpo"><span class="resumo-ref">${esc(i.rotulo)} · ${esc(i.situacao)}</span>`
        + `<b>${esc(i.titulo)}</b><span class="resumo-local">${esc(i.local)}</span><strong>${esc(i.precoCheio)}</strong>`
        + `<span class="resumo-dica">Clique para abrir no site</span></div></div>`;
    estado.resumo = { chave: "resumo", el, pos: new window.google.maps.LatLng(i.lat, i.lng) };
    estado.camada.definir([...estado.camada.marcas.filter((m) => m.chave !== "resumo"), estado.resumo]);
}

function esconderResumo(estado) {
    if (!estado.resumo) return;
    estado.resumo.el.remove();
    estado.camada.marcas = estado.camada.marcas.filter((m) => m.chave !== "resumo");
    estado.resumo = null;
}

export function destruir(elemento) {
    const estado = mapas.get(elemento);
    if (!estado) return;
    document.removeEventListener("keydown", estado.tecla);
    estado.ouvinte.remove();
    estado.lista.close();
    estado.camada.setMap(null);
    window.google.maps.event.clearInstanceListeners(estado.mapa);
    mapas.delete(elemento);
}
