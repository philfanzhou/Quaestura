import httpClient from './httpClient'
import type { Tag, CreateTagRequest, PagedResult, MutationResult } from '../types'

export const tagApi = {
  async list(params: { name?: string; sortBy?: string; page?: number; size?: number }): Promise<PagedResult<Tag>> {
    const response = await httpClient.get<PagedResult<Tag>>('/admin/tags', { params })
    return response.data
  },
  async upsert(request: CreateTagRequest): Promise<MutationResult> {
    const response = await httpClient.post<MutationResult>('/admin/tags', request)
    return response.data
  },
  async delete(id: string): Promise<MutationResult> {
    const response = await httpClient.delete<MutationResult>(`/admin/tags/${id}`)
    return response.data
  },
}
