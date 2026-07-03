// three.js glue for the 3D preview. All geometry math lives in the F# side
// (Geometry.fs); this module only manages the scene, camera and materials.
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

export function createViewer(container) {
  const scene = new THREE.Scene();

  const camera = new THREE.PerspectiveCamera(45, 1, 0.1, 100000);
  camera.up.set(0, 0, 1); // Z-up, matching STL convention
  camera.position.set(90, -90, 80);

  const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
  container.appendChild(renderer.domElement);

  const controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true;
  controls.dampingFactor = 0.08;

  scene.add(new THREE.AmbientLight(0xffffff, 0.55));
  const key = new THREE.DirectionalLight(0xffffff, 1.6);
  key.position.set(1, -1.2, 2.2);
  scene.add(key);
  const fill = new THREE.DirectionalLight(0x8b5cf6, 0.5);
  fill.position.set(-1.5, 1, 0.6);
  scene.add(fill);

  const grid = new THREE.GridHelper(400, 40, 0x8b5cf6, 0x2a2140);
  grid.rotation.x = Math.PI / 2; // into the XY plane (Z-up world)
  grid.material.transparent = true;
  grid.material.opacity = 0.35;
  scene.add(grid);

  const viewer = { scene, camera, renderer, controls, meshes: new Map(), container };

  const resize = () => {
    const w = container.clientWidth;
    const h = container.clientHeight;
    if (w === 0 || h === 0) return;
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
    renderer.setSize(w, h);
  };
  new ResizeObserver(resize).observe(container);
  resize();

  renderer.setAnimationLoop(() => {
    controls.update();
    renderer.render(scene, camera);
  });

  return viewer;
}

export function setMesh(viewer, id, positions, color) {
  const geo = new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(positions), 3));
  geo.computeVertexNormals(); // non-indexed -> per-face normals, flat STL look
  let mesh = viewer.meshes.get(id);
  if (mesh) {
    mesh.geometry.dispose();
    mesh.geometry = geo;
    mesh.material.color.set(color);
  } else {
    const mat = new THREE.MeshStandardMaterial({ color, roughness: 0.55, metalness: 0.1 });
    mesh = new THREE.Mesh(geo, mat);
    viewer.meshes.set(id, mesh);
    viewer.scene.add(mesh);
  }
}

export function setColor(viewer, id, color) {
  const mesh = viewer.meshes.get(id);
  if (mesh) mesh.material.color.set(color);
}

export function removeMesh(viewer, id) {
  const mesh = viewer.meshes.get(id);
  if (!mesh) return;
  viewer.scene.remove(mesh);
  mesh.geometry.dispose();
  mesh.material.dispose();
  viewer.meshes.delete(id);
}

export function clearMeshes(viewer) {
  for (const id of [...viewer.meshes.keys()]) removeMesh(viewer, id);
}

export function fitView(viewer) {
  if (viewer.meshes.size === 0) return;
  const box = new THREE.Box3();
  for (const mesh of viewer.meshes.values()) box.expandByObject(mesh);
  const center = box.getCenter(new THREE.Vector3());
  const sphere = box.getBoundingSphere(new THREE.Sphere());
  const radius = Math.max(sphere.radius, 1);
  const dist = radius / Math.tan((viewer.camera.fov * Math.PI) / 360) * 1.3;
  // Keep the current viewing direction, just re-center and re-distance.
  const dir = viewer.camera.position.clone().sub(viewer.controls.target);
  if (dir.lengthSq() < 1e-6) dir.set(1, -1, 1);
  dir.normalize();
  viewer.camera.position.copy(center.clone().add(dir.multiplyScalar(dist)));
  viewer.camera.near = Math.max(dist / 1000, 0.01);
  viewer.camera.far = dist * 100;
  viewer.camera.updateProjectionMatrix();
  viewer.controls.target.copy(center);
  viewer.controls.update();
}
