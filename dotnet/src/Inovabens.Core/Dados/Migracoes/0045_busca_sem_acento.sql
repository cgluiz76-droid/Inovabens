-- Todas as buscas do sistema passam a ignorar maiúsculas, acentos e "ç" (antes só a busca global, Ctrl+K).
-- busca_normalizar ganha as maiúsculas acentuadas no translate: lower() de letra acentuada depende do LC_CTYPE do
-- banco, então a troca não fica dependente dele. Mesma assinatura e IMMUTABLE (nenhum índice usa a função).
CREATE OR REPLACE FUNCTION busca_normalizar(t text) RETURNS text
    LANGUAGE sql IMMUTABLE PARALLEL SAFE
    AS $$ SELECT translate(lower(t),
        'áàâãäåéèêëíìîïóòôõöúùûüçñýÿÁÀÂÃÄÅÉÈÊËÍÌÎÏÓÒÔÕÖÚÙÛÜÇÑÝ',
        'aaaaaaeeeeiiiiooooouuuucnyyaaaaaaeeeeiiiiooooouuuucny') $$;
