import type { ChangedFile } from './types';

export interface TreeNode {
  /** Display name; compressed chains of single folders show as "a/b/c". */
  name: string;
  /** Full folder path (or file path for leaves). */
  path: string;
  children: TreeNode[];
  file?: ChangedFile;
}

/** Builds a folder tree from changed files, folders first, with single-child folder chains merged. */
export function buildTree(files: ChangedFile[]): TreeNode[] {
  const root: TreeNode = { name: '', path: '', children: [] };
  for (const f of files) {
    const parts = f.path.split('/');
    let node = root;
    for (let i = 0; i < parts.length - 1; i++) {
      const path = parts.slice(0, i + 1).join('/');
      let child = node.children.find((c) => !c.file && c.path === path);
      if (!child) {
        child = { name: parts[i], path, children: [] };
        node.children.push(child);
      }
      node = child;
    }
    node.children.push({ name: parts[parts.length - 1], path: f.path, children: [], file: f });
  }
  const finish = (n: TreeNode): TreeNode => {
    let node = n;
    while (!node.file && node.children.length === 1 && !node.children[0].file && node.path !== '') {
      const only = node.children[0];
      node = { ...only, name: `${node.name}/${only.name}` };
    }
    const children = node.children.map(finish).sort((a, b) => (!!a.file === !!b.file ? a.name.localeCompare(b.name) : a.file ? 1 : -1));
    return { ...node, children };
  };
  return finish(root).children;
}

/** All files below a node. */
export function filesUnder(node: TreeNode): ChangedFile[] {
  return node.file ? [node.file] : node.children.flatMap(filesUnder);
}
