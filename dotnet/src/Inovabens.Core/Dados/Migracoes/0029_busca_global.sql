-- Busca global (Ctrl+K): comparação sem acento e sem maiúsculas, e telefone/CPF só pelos dígitos.
-- Funções imutáveis em SQL puro (sem a extensão unaccent), mapeadas no EF por FuncoesBusca.
CREATE OR REPLACE FUNCTION busca_normalizar(t text) RETURNS text
    LANGUAGE sql IMMUTABLE PARALLEL SAFE
    AS $$ SELECT translate(lower(t), 'áàâãäåéèêëíìîïóòôõöúùûüçñý', 'aaaaaaeeeeiiiiooooouuuucny') $$;

CREATE OR REPLACE FUNCTION busca_digitos(t text) RETURNS text
    LANGUAGE sql IMMUTABLE PARALLEL SAFE
    AS $$ SELECT regexp_replace(t, '[^0-9]', '', 'g') $$;
