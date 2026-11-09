// Arrastar cartões do CRM: o Firefox só inicia o arraste se o evento dragstart gravar algum dado.
// O resto (de onde saiu, onde caiu) é tratado pelo Blazor nos eventos ondragstart/ondrop.
document.addEventListener("dragstart", (e) => {
    const cartao = e.target instanceof Element ? e.target.closest(".cartao-lead[draggable=true]") : null;
    if (!cartao || !e.dataTransfer) return;
    e.dataTransfer.effectAllowed = "move";
    e.dataTransfer.setData("text/plain", "");
});
