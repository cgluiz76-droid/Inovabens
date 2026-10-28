-- Regras de integridade que não cabem no schema declarativo.

-- 1) Totais de aquisição: quando existem itens (impostos/taxas/despesas), eles mandam.
--    total_aquisicao é coluna GERADA a partir dos 4 componentes (ver 0000_init).
CREATE OR REPLACE FUNCTION fn_recalcula_aquisicao() RETURNS trigger AS $$
DECLARE aq uuid;
BEGIN
  aq := COALESCE(NEW.aquisicao_id, OLD.aquisicao_id);
  UPDATE aquisicoes a SET
    impostos          = COALESCE((SELECT SUM(valor) FROM aquisicao_itens WHERE aquisicao_id = aq AND tipo = 'IMPOSTO'), 0),
    taxas             = COALESCE((SELECT SUM(valor) FROM aquisicao_itens WHERE aquisicao_id = aq AND tipo = 'TAXA'), 0),
    despesas_diversas = COALESCE((SELECT SUM(valor) FROM aquisicao_itens WHERE aquisicao_id = aq AND tipo = 'DESPESA'), 0),
    atualizado_em     = now()
  WHERE a.id = aq;
  RETURN NULL;
END; $$ LANGUAGE plpgsql;
--> statement-breakpoint
CREATE TRIGGER trg_aquisicao_itens_recalcula
AFTER INSERT OR UPDATE OR DELETE ON aquisicao_itens
FOR EACH ROW EXECUTE FUNCTION fn_recalcula_aquisicao();
--> statement-breakpoint

-- 2) audit_log é somente-inserção.
CREATE OR REPLACE FUNCTION fn_audit_imutavel() RETURNS trigger AS $$
BEGIN
  RAISE EXCEPTION 'audit_log é somente inserção';
END; $$ LANGUAGE plpgsql;
--> statement-breakpoint
CREATE TRIGGER trg_audit_imutavel
BEFORE UPDATE OR DELETE ON audit_log
FOR EACH ROW EXECUTE FUNCTION fn_audit_imutavel();
