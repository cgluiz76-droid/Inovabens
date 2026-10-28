import {
  Body, Controller, Delete, Get, HttpCode, Module, Param, ParseUUIDPipe, Patch, Post, Put, Query, UploadedFile, UseInterceptors,
} from '@nestjs/common';
import { FileInterceptor } from '@nestjs/platform-express';
import { memoryStorage } from 'multer';
import { z } from 'zod';
import type { AuthUser } from '../common/auth.types';
import { ClientIp, CurrentUser, RequirePermission } from '../common/decorators';
import { ZodPipe } from '../common/zod.pipe';
import { ImoveisService } from './imoveis.service';
import * as S from './imoveis.schemas';

const UUID = new ParseUUIDPipe();

@Controller()
class ImoveisController {
  constructor(private readonly s: ImoveisService) {}

  @Get('imoveis') @RequirePermission('imoveis:ver')
  listar(@CurrentUser() u: AuthUser, @Query(new ZodPipe(S.listaImoveisSchema)) q: z.infer<typeof S.listaImoveisSchema>) { return this.s.listar(u, q); }

  @Post('imoveis') @RequirePermission('imoveis:criar')
  criar(@CurrentUser() u: AuthUser, @Body(new ZodPipe(S.imovelCriar)) b: z.infer<typeof S.imovelCriar>, @ClientIp() ip: string) { return this.s.criar(u, b, ip); }

  @Get('imoveis/:id') @RequirePermission('imoveis:ver')
  obter(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string) { return this.s.detalhe(u, id); }

  @Patch('imoveis/:id') @RequirePermission('imoveis:editar')
  atualizar(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Body(new ZodPipe(S.imovelAtualizar)) b: z.infer<typeof S.imovelAtualizar>, @ClientIp() ip: string) { return this.s.atualizar(u, id, b, ip); }

  @Delete('imoveis/:id') @HttpCode(204) @RequirePermission('imoveis:excluir')
  excluir(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @ClientIp() ip: string) { return this.s.excluir(u, id, ip); }

  @Put('imoveis/:id/caracteristicas') @RequirePermission('imoveis:editar')
  caracteristicas(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Body(new ZodPipe(S.caracteristicasSchema)) b: z.infer<typeof S.caracteristicasSchema>, @ClientIp() ip: string) { return this.s.setCaracteristicas(u, id, b, ip); }

  @Put('imoveis/:id/proprietarios') @RequirePermission('imoveis:editar')
  proprietarios(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Body(new ZodPipe(S.proprietariosSchema)) b: z.infer<typeof S.proprietariosSchema>, @ClientIp() ip: string) { return this.s.setProprietarios(u, id, b, ip); }

  @Put('imoveis/:id/envolvidos') @RequirePermission('imoveis:editar')
  envolvidos(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Body(new ZodPipe(S.envolvidosSchema)) b: z.infer<typeof S.envolvidosSchema>, @ClientIp() ip: string) { return this.s.setEnvolvidos(u, id, b, ip); }

  @Post('imoveis/:id/midias') @RequirePermission('imoveis:editar')
  @UseInterceptors(FileInterceptor('arquivo', { storage: memoryStorage(), limits: { fileSize: 25 * 1024 * 1024 } }))
  enviarMidia(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @UploadedFile() arquivo: Express.Multer.File | undefined, @Body(new ZodPipe(S.midiaUploadSchema)) b: z.infer<typeof S.midiaUploadSchema>) { return this.s.enviarMidia(u, id, arquivo, b); }

  @Patch('midias/:id') @RequirePermission('imoveis:editar')
  atualizarMidia(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Body(new ZodPipe(S.midiaPatchSchema)) b: z.infer<typeof S.midiaPatchSchema>) { return this.s.atualizarMidia(u, id, b); }

  @Delete('midias/:id') @HttpCode(204) @RequirePermission('imoveis:editar')
  removerMidia(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string) { return this.s.removerMidia(u, id); }

  @Get('imoveis/:id/regularizacoes') @RequirePermission('imoveis:ver')
  regularizacoes(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string) { return this.s.listarRegularizacoes(u, id); }

  @Post('imoveis/:id/regularizacoes') @RequirePermission('imoveis:editar')
  criarReg(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Body(new ZodPipe(S.regularizacaoCriar)) b: z.infer<typeof S.regularizacaoCriar>, @ClientIp() ip: string) { return this.s.criarRegularizacao(u, id, b, ip); }

  @Patch('regularizacoes/:id') @RequirePermission('imoveis:editar')
  atualizarReg(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @Body(new ZodPipe(S.regularizacaoAtualizar)) b: z.infer<typeof S.regularizacaoAtualizar>, @ClientIp() ip: string) { return this.s.atualizarRegularizacao(u, id, b, ip); }

  @Delete('regularizacoes/:id') @HttpCode(204) @RequirePermission('imoveis:editar')
  excluirReg(@CurrentUser() u: AuthUser, @Param('id', UUID) id: string, @ClientIp() ip: string) { return this.s.excluirRegularizacao(u, id, ip); }
}

@Module({ controllers: [ImoveisController], providers: [ImoveisService], exports: [ImoveisService] })
export class ImoveisModule {}
