-- Página do imóvel personalizada: HTML e CSS gerados fora do sistema (ferramenta GeradorPaginas) e colados na aba
-- Página. Quando preenchidos, a página pública mostra esse conteúdo no lugar dos blocos padrão. Gravados já limpos.
alter table paginas_imovel add column if not exists html_personalizado text;
alter table paginas_imovel add column if not exists css_personalizado text;
