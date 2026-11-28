import httpClient from './httpClient'
import type { Knowledge, CreateKnowledgeRequest, PagedResult, MutationResult } from '../types'

export const knowledgeApi = {
  async list(params: { parentId?: string; grade?: number; subject?: number; name?: string; page?: number; size?: number }): Promise<PagedResult<Knowledge>> {
    const response = await httpClient.get<PagedResult<Knowledge>>('/admin/knowledges', { params })
    return response.data
  },
  async upsert(request: CreateKnowledgeRequest): Promise<MutationResult> {
    const response = await httpClient.post<MutationResult>('/admin/knowledges', request)
    return response.data
  },
  async delete(id: string): Promise<MutationResult> {
    const response = await httpClient.delete<MutationResult>(`/admin/knowledges/${id}`)
    return response.data
  },
}
